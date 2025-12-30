# pip install llama-index llama-index-vector-stores-chroma llama-index-embeddings-ollama chromadb
# ollama pull nomic-embed-text

import os
import json
import logging
import asyncio
import aiohttp
from aiohttp import web
from typing import List, Dict, Any, Optional
import numpy as np
from sklearn.metrics.pairwise import cosine_similarity
import re
import time
from dataclasses import dataclass
from pathlib import Path

# 配置日志
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

@dataclass
class DocumentChunk:
    id: str
    text: str
    embedding: List[float]
    metadata: Dict[str, Any]

class VectorDB:
    """简单的向量数据库实现"""
    
    def __init__(self, db_path: str = "vector_db.json"):
        self.db_path = Path(db_path)
        self.chunks: List[DocumentChunk] = []
        self.load_db()
    
    def load_db(self):
        """从文件加载向量数据库"""
        if self.db_path.exists():
            try:
                with open(self.db_path, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                    self.chunks = [
                        DocumentChunk(
                            id=item['id'],
                            text=item['text'],
                            embedding=item['embedding'],
                            metadata=item.get('metadata', {})
                        )
                        for item in data
                    ]
                logger.info(f"加载了 {len(self.chunks)} 个文档块")
            except Exception as e:
                logger.error(f"加载向量数据库失败: {e}")
                self.chunks = []
        else:
            logger.info("向量数据库文件不存在，将创建新的数据库")
    
    def save_db(self):
        """保存向量数据库到文件"""
        try:
            data = [
                {
                    'id': chunk.id,
                    'text': chunk.text,
                    'embedding': chunk.embedding,
                    'metadata': chunk.metadata
                }
                for chunk in self.chunks
            ]
            with open(self.db_path, 'w', encoding='utf-8') as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
            logger.info(f"保存了 {len(self.chunks)} 个文档块到数据库")
        except Exception as e:
            logger.error(f"保存向量数据库失败: {e}")
    
    def add_chunk(self, chunk: DocumentChunk):
        """添加文档块到数据库"""
        self.chunks.append(chunk)
    
    def search(self, query_embedding: List[float], top_k: int = 5) -> List[DocumentChunk]:
        """根据查询向量搜索最相似的文档块"""
        if not self.chunks:
            return []
        
        # 将所有存储的嵌入向量转换为numpy数组
        stored_embeddings = np.array([chunk.embedding for chunk in self.chunks])
        query_array = np.array(query_embedding).reshape(1, -1)
        
        # 计算余弦相似度
        similarities = cosine_similarity(query_array, stored_embeddings)[0]
        
        # 获取最相似的top_k个文档块的索引
        top_indices = np.argsort(similarities)[-top_k:][::-1]
        
        # 返回最相似的文档块
        results = []
        for idx in top_indices:
            if similarities[idx] > 0.1:  # 只返回相似度大于0.1的
                results.append(self.chunks[idx])
        
        return results

class OllamaRAGProxy:
    """Ollama RAG代理服务"""
    
    def __init__(self, ollama_host: str = "http://localhost:11434", 
                 vector_db_path: str = "vector_db.json",
                 embedding_model: str = "nomic-embed-text",
                 llm_model: str = "deepseek-r1:8b"):
        self.ollama_host = ollama_host
        self.vector_db = VectorDB(vector_db_path)
        self.embedding_model = embedding_model
        self.llm_model = llm_model
        self.session = None
    
    async def __aenter__(self):
        self.session = aiohttp.ClientSession()
        return self
    
    async def __aexit__(self, exc_type, exc_val, exc_tb):
        if self.session:
            await self.session.close()
    
    async def get_embedding(self, text: str) -> List[float]:
        """获取文本的嵌入向量"""
        try:
            url = f"{self.ollama_host}/api/embeddings"
            payload = {
                "model": self.embedding_model,
                "prompt": text
            }
            
            async with self.session.post(url, json=payload) as response:
                result = await response.json()
                return result.get("embedding", [])
        except Exception as e:
            logger.error(f"获取嵌入向量失败: {e}")
            return []
    
    def chunk_text(self, text: str, chunk_size: int = 500, overlap: int = 50) -> List[str]:
        """将文本切块"""
        chunks = []
        start = 0
        
        while start < len(text):
            end = start + chunk_size
            
            # 如果超出文本长度，调整结束位置
            if end > len(text):
                end = len(text)
            
            chunk = text[start:end]
            chunks.append(chunk)
            
            # 移动起始位置，考虑重叠
            start = end - overlap
            
            # 如果已经到达文本末尾，跳出循环
            if end == len(text):
                break
        
        return chunks
    
    async def process_document(self, file_path: str, doc_id: str = None) -> bool:
        logger.info(file_path)

        """处理文档并添加到向量数据库"""
        try:
            # 读取文档内容
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()
            
            # 生成文档ID
            if not doc_id:
                doc_id = f"doc_{int(time.time())}_{os.path.basename(file_path)}"
            
            # 切分文本
            chunks = self.chunk_text(content)
            logger.info(f"文档切分为 {len(chunks)} 个块")
            
            # 为每个块生成嵌入向量并添加到数据库
            for i, chunk in enumerate(chunks):
                logger.info(f"处理文档块 {i+1}/{len(chunks)}")
                
                embedding = await self.get_embedding(chunk)
                if not embedding:
                    logger.warning(f"无法为块 {i+1} 生成嵌入向量，跳过")
                    continue
                
                chunk_obj = DocumentChunk(
                    id=f"{doc_id}_chunk_{i}",
                    text=chunk,
                    embedding=embedding,
                    metadata={
                        "doc_id": doc_id,
                        "chunk_index": i,
                        "source_file": file_path,
                        "created_at": time.time()
                    }
                )
                
                self.vector_db.add_chunk(chunk_obj)
            
            # 保存数据库
            self.vector_db.save_db()
            logger.info(f"文档 {file_path} 处理完成并添加到向量数据库")
            return True
            
        except Exception as e:
            logger.error(f"处理文档失败: {e}")
            return False
    
    async def search_relevant_chunks(self, query: str, top_k: int = 5) -> List[str]:
        """搜索与查询相关的文档块"""
        # 获取查询的嵌入向量
        query_embedding = await self.get_embedding(query)
        if not query_embedding:
            logger.warning("无法获取查询的嵌入向量")
            return []
        
        # 在向量数据库中搜索
        results = self.vector_db.search(query_embedding, top_k)
        
        # 提取文本内容
        relevant_texts = [chunk.text for chunk in results]
        
        return relevant_texts
    
    async def forward_to_ollama(self, messages: List[Dict[str, str]], stream: bool = False):
        """转发请求到Ollama"""
        try:
            url = f"{self.ollama_host}/api/chat"
            payload = {
                "model": self.llm_model,
                "messages": messages,
                "stream": stream
            }
            
            async with self.session.post(url, json=payload) as response:
                if stream:
                    # 流式响应处理
                    async for chunk in response.content:
                        if chunk:
                            yield chunk
                else:
                    # 非流式响应
                    result = await response.json()
                    yield json.dumps(result).encode()
        except Exception as e:
            logger.error(f"转发到Ollama失败: {e}")
            yield json.dumps({"error": f"Ollama请求失败: {e}"}).encode()
    
    async def rag_chat(self, messages: List[Dict[str, str]]) -> Dict[str, Any]:
        """RAG聊天处理"""
        # 获取最后一条用户消息作为查询
        user_message = ""
        for msg in reversed(messages):
            if msg.get("role") == "user":
                user_message = msg.get("content", "")
                break
        
        if not user_message:
            return {"error": "未找到用户消息"}
        
        # 搜索相关文档
        relevant_chunks = await self.search_relevant_chunks(user_message)
        
        if relevant_chunks:
            # 构建增强的提示
            context = "以下是相关文档信息，可作为回答问题的参考：\n\n"
            context += "\n\n".join([f"文档片段 {i+1}: {chunk}" for i, chunk in enumerate(relevant_chunks)])
            context += "\n\n请基于以上文档信息回答用户的问题。"
            
            # 在原始消息前添加上下文
            enhanced_messages = [
                {"role": "system", "content": context}
            ] + messages
        else:
            # 没有找到相关文档，使用原始消息
            enhanced_messages = messages
        
        # 转发到Ollama
        try:
            async for response_chunk in self.forward_to_ollama(enhanced_messages, stream=False):
                response = json.loads(response_chunk.decode())
                return response
        except Exception as e:
            logger.error(f"RAG聊天处理失败: {e}")
            return {"error": f"RAG处理失败: {e}"}

# 创建web应用
async def create_app():
    app = web.Application()
    
    # 创建全局RAG代理实例（但不立即启动会话）
    rag_proxy = OllamaRAGProxy()
    app['rag_proxy'] = rag_proxy
    
    # 启动时创建会话
    async def startup(app):
        proxy = app['rag_proxy']
        proxy.session = aiohttp.ClientSession()
    
    # 清理时关闭会话
    async def cleanup(app):
        proxy = app['rag_proxy']
        if proxy and proxy.session:
            await proxy.session.close()
    
    app.on_startup.append(startup)
    app.on_cleanup.append(cleanup)
    
    # 添加CORS中间件
    async def cors_middleware(app, handler):
        async def middleware(request):
            response = await handler(request)
            response.headers['Access-Control-Allow-Origin'] = '*'
            response.headers['Access-Control-Allow-Methods'] = 'POST, GET, OPTIONS'
            response.headers['Access-Control-Allow-Headers'] = 'Content-Type'
            return response
        return middleware
    
    app.middlewares.append(cors_middleware)
    
    # 聊天接口 - 兼容OpenAI API格式
    async def chat_handler(request):
        try:
            data = await request.json()
            messages = data.get("messages", [])
            stream = data.get("stream", False)
            
            rag_proxy = request.app['rag_proxy']
            response = await rag_proxy.rag_chat(messages)
            
            return web.json_response(response)
        except Exception as e:
            logger.error(f"聊天处理失败: {e}")
            return web.json_response({"error": str(e)}, status=500)
    
    # 文档处理接口
    async def process_document_handler(request):
        try:
            reader = await request.multipart()
            
            file_part = await reader.next()
            if not file_part or file_part.name != 'file':
                return web.json_response({"error": "缺少文件参数"}, status=400)
            
            # 读取上传的文件内容
            content = await file_part.read(decode=False)
            
            # 创建临时文件
            import tempfile
            with tempfile.NamedTemporaryFile(mode='wb', delete=False, suffix='.txt') as tmp_file:
                tmp_file.write(content)
                tmp_file_path = tmp_file.name
            
            try:
                # 处理文档
                rag_proxy = request.app['rag_proxy']
                success = await rag_proxy.process_document(tmp_file_path)
                
                if success:
                    return web.json_response({"message": "文档处理成功"})
                else:
                    return web.json_response({"error": "文档处理失败"}, status=500)
            finally:
                # 删除临时文件
                os.unlink(tmp_file_path)
                
        except Exception as e:
            logger.error(f"文档处理失败: {e}")
            return web.json_response({"error": str(e)}, status=500)
    
    # 文档处理接口（从URL）
    async def process_document_url_handler(request):
        try:
            data = await request.json()
            file_path = data.get("file_path")
            doc_id = data.get("doc_id")
            
            if not file_path:
                return web.json_response({"error": "缺少文件路径"}, status=400)
            
            rag_proxy = request.app['rag_proxy']
            success = await rag_proxy.process_document(file_path, doc_id)
            
            if success:
                return web.json_response({"message": "文档处理成功"})
            else:
                return web.json_response({"error": "文档处理失败"}, status=500)
                
        except Exception as e:
            logger.error(f"文档处理失败: {e}")
            return web.json_response({"error": str(e)}, status=500)
    
    # 向量数据库状态接口
    async def db_status_handler(request):
        try:
            rag_proxy = request.app['rag_proxy']
            status = {
                "vector_db_size": len(rag_proxy.vector_db.chunks),
                "embedding_model": rag_proxy.embedding_model,
                "llm_model": rag_proxy.llm_model,
                "ollama_host": rag_proxy.ollama_host
            }
            return web.json_response(status)
        except Exception as e:
            logger.error(f"获取数据库状态失败: {e}")
            return web.json_response({"error": str(e)}, status=500)
    
    # 添加路由
    app.router.add_post('/v1/chat/completions', chat_handler)  # 兼容OpenAI API格式
    app.router.add_post('/api/chat', chat_handler)  # 自定义API
    app.router.add_post('/api/process_document', process_document_handler)  # 文件上传处理
    app.router.add_post('/api/process_document_url', process_document_url_handler)  # URL处理
    app.router.add_get('/api/db_status', db_status_handler)  # 数据库状态
    
    return app

def main():
    """主函数"""
    import argparse
    
    parser = argparse.ArgumentParser(description='Ollama RAG代理服务')
    parser.add_argument('--host', default='0.0.0.0', help='服务器主机地址')
    parser.add_argument('--port', type=int, default=8000, help='服务器端口')
    parser.add_argument('--ollama-host', default='http://localhost:11434', help='Ollama服务地址')
    parser.add_argument('--embedding-model', default='nomic-embed-text', help='嵌入模型')
    parser.add_argument('--llm-model', default='deepseek-r1:8b', help='LLM模型')
    parser.add_argument('--vector-db-path', default='vector_db.json', help='向量数据库路径')
    
    args = parser.parse_args()
    
    print(f"启动RAG代理服务...")
    print(f"Ollama服务地址: {args.ollama_host}")
    print(f"嵌入模型: {args.embedding_model}")
    print(f"LLM模型: {args.llm_model}")
    print(f"向量数据库路径: {args.vector_db_path}")
    print(f"服务监听地址: {args.host}:{args.port}")
    
    # 运行web服务器
    app = create_app()
    web.run_app(app, host=args.host, port=args.port)

if __name__ == "__main__":
    main()



