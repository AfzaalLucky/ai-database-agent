/*
  FoundryTrade sample database — 01: (re)create the database.

  DESTRUCTIVE for FoundryTrade only: drops and recreates it so every run starts from the same
  deterministic state. It never touches any other database.
  Run with:  sqlcmd -S localhost -E -C -b -i database/01-create-database.sql
*/
SET NOCOUNT ON;
USE master;
GO

IF DB_ID(N'FoundryTrade') IS NOT NULL
BEGIN
    ALTER DATABASE FoundryTrade SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE FoundryTrade;
END;
GO

CREATE DATABASE FoundryTrade COLLATE SQL_Latin1_General_CP1_CI_AS;
GO

ALTER DATABASE FoundryTrade SET RECOVERY SIMPLE;
ALTER DATABASE FoundryTrade SET READ_COMMITTED_SNAPSHOT ON; -- readers never block writers (or each other)
GO
