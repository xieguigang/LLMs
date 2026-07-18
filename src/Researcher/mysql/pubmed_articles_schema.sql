CREATE DATABASE  IF NOT EXISTS `pubmed_mirror` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci */ /*!80016 DEFAULT ENCRYPTION='N' */;
USE `pubmed_mirror`;
-- MySQL dump 10.13  Distrib 8.0.41, for Win64 (x86_64)
--
-- Host: 127.0.0.1    Database: pubmed_mirror
-- ------------------------------------------------------
-- Server version	8.0.36

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!50503 SET NAMES utf8 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Table structure for table `pubmed_articles`
--

DROP TABLE IF EXISTS `pubmed_articles`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `pubmed_articles` (
  `pmid` varchar(16) COLLATE utf8mb4_unicode_ci NOT NULL COMMENT 'PubMed 唯一标识符',
  `title` text COLLATE utf8mb4_unicode_ci NOT NULL COMMENT '文献标题',
  `authors` text COLLATE utf8mb4_unicode_ci COMMENT '作者列表（多个作者以分隔符存储）',
  `journal` varchar(500) COLLATE utf8mb4_unicode_ci DEFAULT NULL COMMENT '期刊名称',
  `pub_year` int unsigned DEFAULT NULL COMMENT '发表年份',
  `doi` varchar(255) COLLATE utf8mb4_unicode_ci DEFAULT NULL COMMENT '数字对象标识符 (DOI)',
  `keywords` text COLLATE utf8mb4_unicode_ci COMMENT '作者关键词列表',
  `mesh_terms` text COLLATE utf8mb4_unicode_ci COMMENT 'MeSH 主题词列表',
  `created_at` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '记录入库时间',
  `updated_at` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '记录更新时间',
  `abstract` mediumtext COLLATE utf8mb4_unicode_ci COMMENT '文献摘要',
  `full_text` longtext COLLATE utf8mb4_unicode_ci COMMENT '文献全文',
  PRIMARY KEY (`pmid`),
  KEY `idx_pub_year` (`pub_year`),
  KEY `idx_pub_year_pmid` (`pub_year` DESC,`pmid` DESC),
  KEY `idx_doi` (`doi`),
  KEY `idx_journal` (`journal`),
  FULLTEXT KEY `ft_title` (`title`) /*!50100 WITH PARSER `ngram` */ ,
  FULLTEXT KEY `ft_abstract` (`abstract`) /*!50100 WITH PARSER `ngram` */ ,
  FULLTEXT KEY `ft_keywords` (`keywords`) /*!50100 WITH PARSER `ngram` */ ,
  FULLTEXT KEY `ft_mesh_terms` (`mesh_terms`) /*!50100 WITH PARSER `ngram` */ ,
  FULLTEXT KEY `ft_multi` (`title`,`abstract`,`keywords`,`mesh_terms`) /*!50100 WITH PARSER `ngram` */ 
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='PubMed 文献主表（NCBI PubMed 本地镜像）';
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping events for database 'pubmed_mirror'
--

--
-- Dumping routines for database 'pubmed_mirror'
--
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-07-18 20:38:47
