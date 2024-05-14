﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using CommandLine;
using CommandLine.Text;

namespace DBFImport
{
    class Program
    {

        class Options
        {
            [Option('p', "path", Required = true, HelpText = "Path to DBF file(s)")]
            public string DbfPath { get; set; }

            [Option('s', "server", SetName = "server&db", Required = true, HelpText = "SQL Server (and instance)")]
            public string Server { get; set; }

            [Option('d', "database", SetName = "server&db", Required = true, HelpText = "Database name")]
            public string Database { get; set; }

            [Option('c', "connectionstring", SetName = "connstring", Required = true, HelpText = "Database connection string")]
            public string ConnectionString { get; set; }

            [Option("codepage", Required = false, HelpText = "Code page for decoding text")]
            public int CodePage { get; set; }

            [Option("nobulkcopy", Required = false, HelpText = "Use much slower 'SQL Command' interface, instead of 'SQL BulkCopy'")]
            public bool NoBulkCopy { get; set; }

            [Option("nologo", Required = false, HelpText = "Don't show application title and copyright")]
            public bool NoLogo { get; set; }

            [Option("create", Required = false, HelpText = "Crear tablas")]
            public bool CreateTable { get; set; }

            [Option("deleteInfo", Required = false, HelpText = "Limpiar tablas")]
            public bool deleteTable { get; set; }

            [Usage]
            public static IEnumerable<Example> Examples
            {
                get
                {
                    return new List<Example>() {
                        new Example("Imports DBF files",
                            new Options
                            {
                               DbfPath = @"C:\DBFs\*.DBF",
                                Server = @"MIBOFFILL\SQLEXPRESS",
                                Database = "dbfToSQL",
                            }),
                        //new Example("Imports DBF files, and decode text using code page 1252",
                        //    new Options
                        //    {
                        //        DbfPath = @"c:\Data\My DBF files\*.DBF",
                        //        Server = @"DEVSERVER\SQL2017",
                        //        Database = "ImportedDbfFiles",
                        //        CodePage = 1252
                        //    }),
                    //    new Example("Imports DBF files, and connect to SQL Server using connection string",
                    //        new Options
                    //        {
                    //            DbfPath = @"C:\DBFs\*.DBF",
                    //            ConnectionString = @"Server=MIBOFFILL\SQLEXPRESS;Database=dbfToSQL",
                    //       }),
                    };
                }
            }
        }

        public static int Main(string[] args)
        {
            return Parser.Default
                .ParseArguments<Options>(args)
                .MapResult(
                    o => RunWithOptions(o),
                    errs => 1);
        }

        private static void LogException(string message, Exception e)
        {
            Console.WriteLine(message);
            Console.WriteLine($"   Exception: {e.Message}");
            while (e.InnerException != null)
            {
                e = e.InnerException;
                Console.WriteLine($"       Inner: {e.Message}");
            }
        }

        private static int RunWithOptions(Options options)
        {
            if (!options.NoLogo)
            {
                Console.WriteLine(CommandLine.Text.HeadingInfo.Default.ToString());
                Console.WriteLine(CommandLine.Text.CopyrightInfo.Default.ToString());
            }

            string dbfPath = options.DbfPath;

            string connectionString = options.ConnectionString;
            if (string.IsNullOrEmpty(connectionString))
            {
                SqlConnectionStringBuilder connectionStringBuilder = new SqlConnectionStringBuilder();
                connectionStringBuilder.DataSource = options.Server;
                connectionStringBuilder.InitialCatalog = options.Database;
                connectionStringBuilder.IntegratedSecurity = true;
                connectionString = connectionStringBuilder.ConnectionString;
            }

            int codepage = options.CodePage;
            bool noBulkCopy = true;
            bool createTable = options.CreateTable;
            bool deleteTable = options.deleteTable;

            int failedFiles = 0;
            int succeededFiles = 0;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                int totalInsertCount = 0;
                if (File.Exists(dbfPath))
                {
                    int insertCount = ProcessFile(dbfPath, connectionString, codepage, noBulkCopy, createTable, deleteTable);
                    if (insertCount >= 0)
                    {
                        totalInsertCount += insertCount;
                        succeededFiles++;
                    }
                    else
                    {
                        failedFiles++;
                    }
                }
                else
                {
                    string path;
                    string mask;
                    if (Directory.Exists(dbfPath))
                    {
                        path = dbfPath;
                        mask = "*.DBF";
                    }
                    else
                    {
                        mask = Path.GetFileName(dbfPath);
                        path = Path.GetDirectoryName(dbfPath);
                        if (string.IsNullOrEmpty(path))
                            path = ".";
                    }

                    foreach (var file in Directory.EnumerateFiles(path, mask))
                    {
                        int insertCount = ProcessFile(file, connectionString, codepage, noBulkCopy, createTable, deleteTable);
                        if (insertCount >= 0)
                        {
                            totalInsertCount += insertCount;
                            succeededFiles++;
                        }
                        else
                        {
                            failedFiles++;
                        }
                    }
                }

                if (succeededFiles == 0)
                {
                    throw new Exception("No files were successfully imported");
                }

                Console.WriteLine();
                Console.WriteLine("Import finished.");
                Console.WriteLine("Statistics:");
                Console.WriteLine($"  Records:          {totalInsertCount}");
                Console.WriteLine($"  Succeeded files:  {succeededFiles}");
                Console.WriteLine($"  Failed files:     {failedFiles}");
                Console.WriteLine($"  Total Duration:   {sw.Elapsed}");
            }
            catch (Exception e)
            {
                LogException($"Failed to process files {dbfPath}", e);

#if DEBUG
                throw;
#else
                return -1;
#endif
            }

            return failedFiles;
        }

        static int ProcessFile(string filename, string connectionString, int codepage, bool noBulkCopy, bool createTable, bool deleteInfo)
        {
            Console.WriteLine($"Processing {filename}...");
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                using (IFileStream dbfFileStream = FileStreamFactory.Create(filename, codepage))
                {
                    string table = Path.GetFileNameWithoutExtension(filename);

                    Console.WriteLine($"  LastUpdate:       {dbfFileStream.Header.LastUpdate.ToShortDateString()}");
                    Console.WriteLine($"  Fields:           {dbfFileStream.Header.FieldCount}");
                    var recordCount = dbfFileStream.Header.RecordCount;
                    if (recordCount.HasValue)
                        Console.WriteLine($"  Records:          {recordCount.Value}");
                    Console.Write("  Importing:        ");

                    int insertCount = CreateTable(connectionString, table, dbfFileStream.FieldDescriptors, dbfFileStream.Records, noBulkCopy, createTable, deleteInfo);
                    Console.WriteLine($"  Inserted:         {insertCount}");
                    Console.WriteLine($"  Duration:         {sw.Elapsed}");

                    return insertCount;
                }
            }
            catch (Exception e)
            {
                LogException($"Failed to process file {filename}", e);

#if DEBUG
                throw;
#else
                return -1;
#endif
            }
        }

        private static int CreateTable(string connectionString, string table, 
            IReadOnlyList<IFieldDescriptor> fieldDescriptors, IEnumerable<Record> records,
            bool noBulkCopy, bool createTable, bool deleteInfo)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                try
                {
                    conn.Open();
                }
                catch (Exception e)
                {
                    throw new Exception("Failed to connect to database", e);
                }

                if (createTable)
                {
                    try
                    {
                        using (SqlCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"IF OBJECT_ID('{table}', 'U') IS NOT NULL DROP TABLE [{table}];";
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Failed to drop existing table {table}", e);
                    }

                    try
                    {
                        using (SqlCommand cmd = conn.CreateCommand())
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine($"CREATE TABLE [{table}] ([id_{table.ToLower()}] [int] CONSTRAINT pk_ven_{table.ToLower()} PRIMARY KEY CLUSTERED IDENTITY(1,1) NOT NULL, ");
                            bool first = true;
                            foreach (var fieldDescriptor in fieldDescriptors)
                            {
                                if (first)
                                    first = false;
                                else
                                    sb.Append(", ");
                                sb.AppendLine($"[{fieldDescriptor.Name.ToLower()}] {fieldDescriptor.GetSqlDataType()} NOT NULL");
                            }
                            sb.AppendLine($")");
                            cmd.CommandText = sb.ToString();
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Failed to create table {table}", e);
                    }
                }

                if (deleteInfo)
                {
                    try
                    {
                        using (SqlCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"DELETE FROM {table}";
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Failed to delete info table {table}", e);
                    }
                }


                try
                {
                    if (noBulkCopy)
                        return FillTableUsingSqlCommand(conn, table, fieldDescriptors, records);
                    else
                        return FillTableUsingBulkCopy(conn, table, fieldDescriptors, records);
                }
                catch (Exception e)
                {
                    throw new Exception($"Failed to fill table {table}", e);
                }
            }
        }

        private static int FillTableUsingBulkCopy(
            SqlConnection conn, string table, IReadOnlyList<IFieldDescriptor> fieldDescriptors,
            IEnumerable<Record> records)
        {
            using (SqlBulkCopy bcp =  new SqlBulkCopy(conn))
            {
                bcp.DestinationTableName = $"[{table}]";
                bcp.BulkCopyTimeout = 1800;

                DataReader dataReader = new DataReader(fieldDescriptors, records);

                try
                {
                    bcp.BatchSize = 10000;
                    bcp.NotifyAfter = 1000;
                    bcp.SqlRowsCopied += delegate (object sender, SqlRowsCopiedEventArgs args) { Console.Write('.'); };
                    bcp.WriteToServer(dataReader);
                }
                finally 
                {
                    Console.WriteLine();
                }

                return dataReader.Inserted;
            }
        }

        private static int FillTableUsingSqlCommand(
            SqlConnection conn, string table, IReadOnlyList<IFieldDescriptor> fieldDescriptors,
            IEnumerable<Record> records)
        {
            using (SqlCommand cmd = conn.CreateCommand())
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"INSERT INTO [{table}] (");
                bool first = true;
                foreach (var fieldDescriptor in fieldDescriptors)
                {
                    if (first)
                        first = false;
                    else
                        sb.Append(", ");
                    sb.Append($"[{fieldDescriptor.Name}]");
                }

                sb.AppendLine($") VALUES (");
                int no = 0;
                first = true;
                foreach (var fieldDescriptor in fieldDescriptors)
                {
                    if (first)
                        first = false;
                    else
                        sb.Append(", ");
                    sb.Append($"@{fieldDescriptors[no].Name}");

                    cmd.Parameters.Add(fieldDescriptor.GetSqlParameter($"@{fieldDescriptors[no].Name}"));

                    no++;
                }

                sb.AppendLine($")");
                cmd.CommandText = sb.ToString();
                int insertCount = 0;
                using (var transaction = conn.BeginTransaction())
                {
                    foreach (var record in records)
                    {
                        try
                        {
                            no = 0;
                            foreach (var field in record.Fields)
                            {
                                object defaultValue = null;
                                if (field == null)
                                {
                                    var a = fieldDescriptors[no].GetSqlDataType();
                                    if (a.Contains("DECIMAL") || a.Contains("INT") || a.Contains("FLOAT"))
                                    {
                                        defaultValue = 0;
                                    }
                                    if (a.Contains("VARCHAR"))
                                    {
                                        defaultValue = "";
                                    }
                                    
                                }
                               
                                
                                cmd.Parameters[$"@{fieldDescriptors[no].Name}"].Value = field ?? (defaultValue ?? DBNull.Value);
                                no++;
                            }
                            //foreach (SqlParameter parameter in cmd.Parameters)
                            //{
                            //    Console.WriteLine(parameter.ParameterName + ": " + parameter.Value);
                            //}
                            cmd.Transaction = transaction;
                            cmd.ExecuteNonQuery();
                            insertCount++;
                        }
                        catch (Exception e)
                        {
                            throw new Exception(
                                $"Failed to insert record #{record.RecordNo + 1} into database, {insertCount} already inserted",
                                e);
                        }

                        if (insertCount % 1000 == 0)
                        {
                            Console.Write('.');
                        }
                    }

                    Console.WriteLine();
                    transaction.Commit();
                }

                return insertCount;
            }
        }
    }
}
