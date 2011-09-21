﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Helvartis.SQLServerDump.Properties;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace Helvartis.SQLServerDump
{
    class Program
    {
        public const String PRODUCT_VERSION = "0.0.1";
        private SQLServerDumpArguments arguments;

        public static void Main(string[] args)
        {
            new Program().run(args);
        }

        public void run(string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return;
            }
            try {
                arguments = new SQLServerDumpArguments(args);
            } catch (SQLServerDumpArgumentsException ex) {
                Console.Error.WriteLine(ex.Message);
                return;
            }

            if (arguments.WrongOptions)
            {
                ShowUsage();
                return;
            }

            if (arguments.ShowHelp)
            {
                ShowHelp();
                return;
            }

            if (arguments.ServerName == null)
            {
                DataTable availableSqlServers = SmoApplication.EnumAvailableSqlServers(true);
                if (availableSqlServers.Rows.Count > 1)
                {
                    Console.Error.WriteLine(Resources.ErrMoreThanOneLocalInstance);
                    return;
                }
                if (availableSqlServers.Rows.Count == 0)
                {
                    Console.Error.WriteLine(Resources.ErrNoLocalInstance);
                    return;
                }
                arguments.ServerName = availableSqlServers.Rows[0].Field<String>("Name");
            }
            else if (!arguments.ServerName.Contains('\\'))
            {
                arguments.ServerName = ".\\" + arguments.ServerName;
            }

            Server server;
            try
            {
                server = new Server(arguments.ServerName);
                server.Databases.Refresh(); // Try to connect to server
            }
            catch (ConnectionFailureException)
            {
                Console.Error.WriteLine("Could not connect to server " + arguments.ServerName);
                return;
            }
            
            if (arguments.Databases.Length == 0)
            {
                LinkedList<string> dbs = new LinkedList<string>();
                foreach (Database db in server.Databases) {
                    if (arguments.IncludeSystemDatabases || !db.IsSystemObject)
                    {
                        dbs.AddLast(db.Name);
                    }
                }
                arguments.Databases = dbs.ToArray();
            } else {
                // Check if databases exist
                bool hasError = false;
                foreach (string dbName in arguments.Databases)
                {
                    if (!server.Databases.Contains(dbName))
                    {
                        Console.Error.WriteLine(String.Format("database '{0}' doesn't exist", dbName));
                        hasError = true;
                    }
                    else if (arguments.DatabaseObjects != null)
                    {
                        foreach (string objectName in arguments.DatabaseObjects)
                        {
                            if (!ContainsObject(server.Databases[dbName], objectName))
                            {
                                Console.Error.WriteLine(String.Format("object '{0}' doesn't exist in database '{1}'", objectName, dbName));
                                hasError = true;
                            }
                        }
                    }
                }
                if (hasError)
                {
                    return;
                }
            }

            Scripter scrp = new Scripter(server)
            {
                Options = new ScriptingOptions()
                {
                    ScriptSchema = true,
                    ScriptData = true,
                    ScriptBatchTerminator = true,
                    ScriptDataCompression = true,
                    ScriptOwner = true,
                    IncludeIfNotExists = true,
                    DriAll = true
                }
            };

            // Where to direct output
            TextWriter output;
            if (arguments.ResultFile != null)
            {
                output = new StreamWriter(arguments.ResultFile);
            }
            else
            {
                output = System.Console.Out;
            }

            foreach (string dbName in arguments.Databases)
            {
                Database db = server.Databases[dbName];
                String header = "-- DATABASE\n";
                Output(db, output, scrp, null, ref header);
                output.WriteLine(String.Format("USE {0};", db.Name));
                Output(db.Tables, output, scrp, "-- TABLES\n");
                Output(db.Views, output, scrp, "-- VIEWS\n");
                Output(db.UserDefinedFunctions, output, scrp, "-- USER DEFINED FUNCTIONS\n");
                Output(db.StoredProcedures, output, scrp, "-- STORED PROCEDURES\n");
                Output(db.Synonyms, output, scrp, "-- SYNONYMS\n");
                Output(db.Triggers, output, scrp, "-- TRIGGERS\n");
            }
            output.Close();
        }

        private void ShowHelp()
        {
            Console.Out.Write(Resources.Help.Replace("{version}", PRODUCT_VERSION).Replace("{usage}",Resources.Usage));
        }
        private void ShowUsage()
        {
            Console.Out.Write(Resources.Usage+"\n"+Resources.Usage_more);
        }
        private bool OutputAtEnd(SmoObjectBase o, string s)
        {
            return o is Table && s.Contains("\nALTER") && !s.StartsWith("INSERT");
        }
        private void Output(SmoCollectionBase coll, TextWriter tw ,Scripter scrp, String header = null)
        {
            LinkedList<string> tableAlterings = new LinkedList<string>();
            foreach (ScriptSchemaObjectBase o in coll)
            {
                Output(o, tw, scrp, tableAlterings, ref header);
            }
            foreach (string s in tableAlterings)
            {
                if (header != null)
                {
                    tw.WriteLine(header);
                }
                tw.WriteLine(s);
            }
        }
        private void Output(NamedSmoObject obj, TextWriter tw, Scripter scrp, LinkedList<string> outputAtEnd, ref String header)
        {
            if (
                (!(bool)obj.Properties["IsSystemObject"].Value || IncludeSysObject(obj))
                    &&
                IncludeObject(obj)
            )
            {
                foreach (string s in scrp.EnumScript(new Urn[] { obj.Urn }))
                {
                    if (outputAtEnd != null && OutputAtEnd(obj, s))
                    {
                        outputAtEnd.AddLast(s.TrimEnd() + ";");
                    }
                    else
                    {
                        if (header != null)
                        {
                            tw.WriteLine(header);
                            header = null;
                        }
                        tw.WriteLine(s.TrimEnd() + ";");
                    }
                }
            }
        }
        private bool IncludeSysObject(SmoObjectBase o)
        {
            return arguments.IncludeSystemObjects || (o is Table ? ((Table)o).Name == "sysdiagrams" : false);
        }
        private bool IncludeObject(NamedSmoObject obj)
        {
            return arguments.DatabaseObjects == null || arguments.DatabaseObjects.Contains(obj.Name) || arguments.DatabaseObjects.Contains(obj.ToString()) || arguments.DatabaseObjects.Contains(obj.ToString().Replace("[","").Replace("]",""));
        }
        private bool ContainsObject(Database database, String objectName)
        {
            return database.Tables.Contains(objectName) ||
                database.Views.Contains(objectName) ||
                database.UserDefinedFunctions.Contains(objectName) ||
                database.StoredProcedures.Contains(objectName) ||
                database.Synonyms.Contains(objectName) ||
                database.Triggers.Contains(objectName);
        }
    }
}