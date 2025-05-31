// export R# package module type define for javascript/typescript language
//
//    imports "ollama" from "Agent";
//
// ref=Agent.OLlamaDemo@Agent, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null

/**
 * 
*/
declare namespace ollama {
   /**
     * @param args default value Is ``null``.
     * @param fcall default value Is ``null``.
     * @param env default value Is ``null``.
   */
   function add_tool(model: object, name: string, desc: string, requires: any, args?: object, fcall?: any, env?: object): any;
   /**
    * chat with the LLMs model throught the ollama client
    * 
    * 
     * @param model -
     * @param msg -
     * @return a tuple list that contains the LLMs result output:
     *  
     *  1. output - the LLMs thinking and LLMs @``T:Ollama.DeepSeekResponse`` message
     *  2. function_calls - the @``T:Ollama.JSON.FunctionCall.FunctionCall`` during the LLMs thinking
   */
   function chat(model: object, msg: string): object;
   /**
     * @param ollama_serve default value Is ``'127.0.0.1:11434'``.
     * @param model default value Is ``'deepseek-r1:671b'``.
   */
   function deepseek_chat(message: string, ollama_serve?: string, model?: string): object;
   /**
    * Create a new ollama client for LLMs chat
    * 
    * 
     * @param model -
     * @param ollama_server -
     * 
     * + default value Is ``'127.0.0.1:11434'``.
     * @param max_memory_size -
     * 
     * + default value Is ``1000``.
     * @param logfile -
     * 
     * + default value Is ``null``.
   */
   function new(model: string, ollama_server?: string, max_memory_size?: object, logfile?: string): object;
   /**
    * set or get the system message for the ollama client
    * 
    * 
     * @param model -
     * @param msg -
     * @param env 
     * + default value Is ``null``.
   */
   function system_message(model: object, msg: any, env?: object): any;
}
