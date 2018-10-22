﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Uno.SourceGeneration.Helpers;
using Uno.SourceGeneratorTasks;
using Uno.SourceGeneratorTasks.Helpers;

namespace Uno.SourceGeneration.Host
{
	class Program
	{
		static int Main(string[] args)
		{
			try
			{
				// Uncomment this for easier debugging
				// Debugger.Launch();

				if (args.Length < 3 || args.Length > 4)
				{
					throw new Exception($"Response file, output path and binlog path are required.");
				}

				var responseFilePath = args[0];
				var generatedFilesOutputPath = args[1];
				var binlogOutputPath = args[2];
				var enableConsole = args.ElementAtOrDefault(3)?.Equals("-console", StringComparison.OrdinalIgnoreCase) ?? false;

				if (enableConsole)
				{
					LogExtensionPoint.AmbientLoggerFactory.AddProvider(new ConsoleLoggerProvider((t, l) => true, true));
				}

				using (var responseFile = File.OpenRead(responseFilePath))
				{
					var env = new DataContractSerializer(typeof(BuildEnvironment));

					if (env.ReadObject(responseFile) is BuildEnvironment environment)
					{
						AssemblyResolver.RegisterAssemblyLoader(environment);

						return RunGeneration(generatedFilesOutputPath, binlogOutputPath, environment, enableConsole);
					}

					return 1;
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(e.ToString());
				return 2;
			}
		}

		/// <summary>
		/// Runs the source generation. This code has to be in a method so the AssemblyResolver
		/// can resolve the dependencies in a proper order.
		/// </summary>
		private static int RunGeneration(string generatedFilesOutputPath, string binlogOutputPath, BuildEnvironment environment, bool enableConsole)
		{
			using (var logger = new BinaryLoggerForwarderProvider(binlogOutputPath))
			{
				typeof(Program).Log().Info($"Generating files to path {generatedFilesOutputPath}, logoutput={binlogOutputPath}");

				try
				{
					var host = new SourceGeneratorHost(environment);

					var generatedFiles = host.Generate();

					File.WriteAllText(generatedFilesOutputPath, string.Join(";", generatedFiles));

					return 0;
				}
				catch (Exception e)
				{
					if (enableConsole)
					{
						Console.WriteLine(e.ToString());
					}

					typeof(Program).Log().Error("Generation failed: " + e.ToString());

					return 3;
				}
			}
		}
	}
}