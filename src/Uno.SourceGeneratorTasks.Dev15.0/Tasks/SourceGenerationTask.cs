// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.Serialization;
using Uno.SourceGeneration.Helpers;
using System.Threading;
using Uno.SourceGeneration.Host.Helpers;
using Uno.SourceGeneration.Host.GenerationClient;
using Uno.SourceGeneration.Host.Messages;
using Uno.SourceGeneratorTasks.Helpers;
using System.Runtime.InteropServices;

[assembly: CommitHashAttribute("<developer build>")]

namespace Uno.SourceGeneratorTasks
{
	public class SourceGenerationTask_v0 : Microsoft.Build.Utilities.Task
	{
		[Required]
		public string ProjectFile { get; set; }

		[Required]
		public string Platform { get; set; }

		[Required]
		public string Configuration { get; set; }

		public string TargetFramework { get; set; }

		public string VisualStudioVersion { get; set; }

		public string TargetFrameworkRootPath { get; set; }

		/// <summary>
		/// Capture the generation host standard output for debug purposes.
		/// (used when <see cref="UseGenerationController"/> is set to false)
		/// </summary>
		public string CaptureGenerationHostOutput { get; set; }

		/// <summary>
		/// Enables the use of the GenerationController mode.
		/// </summary>
		public string UseGenerationController { get; set; } = bool.TrueString;

		/// <summary>
		/// Provides a list of assemblies to be loaded in the SourceGenerator
		/// secondary app domains. This is a backward compatibility feature related
		/// to the use of external libraries in previous versions of the SourceGeneration task.
		/// </summary>
		public string[] AdditionalAssemblies { get; set; }

		[Required]
		public string[] SourceGenerators { get; set; }

		public string OutputPath { get; set; }

		public string BinLogOutputPath { get; set; }

		public bool BinLogEnabled { get; set; }

		public string SharedGenerationId { get; set; }

		[Output]
		public string[] GenereratedFiles { get; set; }

		private CancellationTokenSource _sharedCompileCts;

		public override bool Execute()
		{
			string lockFile = null;

			// Debugger.Launch();

			try
			{
				lockFile = Path.Combine(OutputPath, "unoGenerator.lock");

				if (File.Exists(lockFile))
				{
					// This may happen during the initial load of the project. At this point
					// there is no need to re-generate the files.
					return true;
				}

				if (SupportsGenerationController)
				{
					GenerateWithHostController();
				}
				else
				{
					GenerateWithHost();
				}

				return true;
			}
			catch (Exception e)
			{
				var aggregate = e as AggregateException;

				if (aggregate != null)
				{
					this.Log.LogError(string.Join(", ", aggregate.InnerExceptions.Select(ie => ie.Message)));
				}
				else
				{
					this.Log.LogError(e.Message);
				}

				this.Log.LogMessage(MessageImportance.Low, e.ToString());

				return false;
			}
			finally
			{
				if (File.Exists(lockFile))
				{
					File.Delete(lockFile);
				}
			}
		}
		public bool SupportsGenerationController
			=> (bool.TryParse(UseGenerationController, out var result) && result)
			&& (
				!RuntimeHelpers.IsMono
				|| RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				|| RuntimeHelpers.IsNetCore
			);


		private void GenerateWithHostController()
		{
			Log.LogMessage(MessageImportance.Low, "Using host controller generation mode");

			var taskLogger = new TaskLoggerProvider() { TaskLog = Log };
			LogExtensionPoint.AmbientLoggerFactory.AddProvider(taskLogger);

			using (_sharedCompileCts = new CancellationTokenSource())
			{
				var responseFile = Path.GetTempFileName();
				var outputFile = Path.GetTempFileName();
				var binlogFile = Path.GetTempFileName() + ".binlog";

				try
				{ 
					var buildEnvironment = CreateBuildEnvironment();

					using (var responseStream = File.OpenWrite(responseFile))
					{
						var serializer = new DataContractSerializer(typeof(BuildEnvironment));
						serializer.WriteObject(responseStream, buildEnvironment);
					}

					// Note: using ToolArguments here (the property) since
					// commandLineCommands (the parameter) may have been mucked with
					// (to support using the dotnet cli)
					var responseTask = GenerationServerConnection.RunServerGeneration(
						GenerateServerId(buildEnvironment),
						new List<string> { responseFile, outputFile, binlogFile },
						new GenerationsPathsInfo(GetHostPath(), OutputPath, Path.GetTempPath()),
						keepAlive: null,
						cancellationToken: _sharedCompileCts.Token);

					responseTask.Wait(_sharedCompileCts.Token);

					BinaryLoggerReplayHelper.Replay(BuildEngine, binlogFile);

					if (responseTask.Result.Type == GenerationResponse.ResponseType.Completed)
					{
						GenereratedFiles = File.ReadAllText(outputFile).Split(';');
					}
					else
					{
						throw new InvalidOperationException($"Generation failed, error code {responseTask.Result.Type}");
					}
				}
				finally
				{
					File.Delete(responseFile);
					File.Delete(outputFile);
					File.Delete(binlogFile);
				}
			}
		}

		private string GenerateServerId(BuildEnvironment buildEnvironment)
		{
			return GenerationServerConnection.GetPipeNameForPathOpt(
				string.Concat(
					GetHostPath(),
					// Don't include the process ID since the server only processes one client at a time.
					// Process.GetCurrentProcess().Id.ToString(),
					buildEnvironment.ProjectFile,
					buildEnvironment.Platform,
					string.Join(",", buildEnvironment.SourceGenerators)
				)
			);
		}

		private void GenerateWithHost()
		{
			Log.LogMessage(MessageImportance.Low, $"Using single-use host generation mode");

var captureHostOutput = false;
			if (!bool.TryParse(this.CaptureGenerationHostOutput, out captureHostOutput))
			{
#if DEBUG
				captureHostOutput = true; // default to true in DEBUG
#else
				captureHostOutput = false; // default to false in RELEASE
#endif
			}

			var hostPath = GetHostPath();

			var responseFile = Path.GetTempFileName();
			var outputFile = Path.GetTempFileName();
			var binlogFile = Path.GetTempFileName() + ".binlog";

			try
			{
				using (var responseStream = File.OpenWrite(responseFile))
				{
					var serializer = new DataContractSerializer(typeof(BuildEnvironment));
					serializer.WriteObject(responseStream, CreateBuildEnvironment());
				}

				ProcessStartInfo buildInfo()
				{
					if (RuntimeHelpers.IsNetCore)
					{
						var hostBinPath = Path.Combine(hostPath, "Uno.SourceGeneration.Host.dll");
						string arguments = $"\"{hostBinPath}\" \"{responseFile}\" \"{outputFile}\" \"{binlogFile}\"";
						var pi = new ProcessStartInfo("dotnet", arguments)
						{
							UseShellExecute = false,
							CreateNoWindow = true,
							WindowStyle = ProcessWindowStyle.Hidden,
						};

						return pi;
					}
					else
					{
						var hostBinPath = Path.Combine(hostPath, "Uno.SourceGeneration.Host.exe");
						string arguments = $"\"{responseFile}\" \"{outputFile}\" \"{binlogFile}\"";

						var pi = new ProcessStartInfo(hostBinPath, arguments)
						{
							UseShellExecute = false,
							CreateNoWindow = true,
							WindowStyle = ProcessWindowStyle.Hidden,
						};

						return pi;
					}
				}

				using (var process = new Process())
				{
					var startInfo = buildInfo();

					if (captureHostOutput)
					{
						startInfo.Arguments += " -console";
						startInfo.RedirectStandardOutput = true;
						startInfo.RedirectStandardError = true;

						process.StartInfo = startInfo;
						process.Start();

						var output = process.StandardOutput.ReadToEnd();
						var error = process.StandardError.ReadToEnd();
						process.WaitForExit();

						this.Log().Info(
							$"Executing {startInfo.FileName} {startInfo.Arguments}:\n" +
							$"result: {process.ExitCode}\n" +
							$"\n---begin host output---\n{output}\n" +
							$"---begin host ERROR output---\n{error}\n" +
							"---end host output---\n");
					}
					else
					{
						process.StartInfo = startInfo;
						process.Start();
						process.WaitForExit();
					}

					BinaryLoggerReplayHelper.Replay(BuildEngine, binlogFile);

					if (process.ExitCode == 0)
					{
						GenereratedFiles = File.ReadAllText(outputFile).Split(';');
					}
					else
					{
						throw new InvalidOperationException($"Generation failed, error code {process.ExitCode}");
					}
				}
			}
			finally
			{
				File.Delete(responseFile);
				File.Delete(outputFile);
				File.Delete(binlogFile);
			}
		}

		private string GetHostPath()
		{
			var currentPath = Path.GetDirectoryName(new Uri(GetType().Assembly.CodeBase).LocalPath);
			var hostPlatform = RuntimeHelpers.IsNetCore ? "netcoreapp2.1" : "net461";
			var installedPath = Path.Combine(currentPath, "..", "host", hostPlatform);
#if DEBUG
			var configuration = "Debug";
#else
			var configuration = "Release";
#endif

			var devPath = Path.Combine(currentPath, "..", "..", "..", "Uno.SourceGeneration.Host", "bin", configuration, hostPlatform);

			if (Directory.Exists(devPath))
			{
				return devPath;
			}
			else if (Directory.Exists(installedPath))
			{
				return installedPath;
			}
			else
			{
				throw new InvalidOperationException($"Unable to find Uno.SourceGeneration.Host.dll (in {devPath} or {installedPath})");
			}
			
		}

		private BuildEnvironment CreateBuildEnvironment()
			=> new BuildEnvironment
			{
				Configuration = Configuration,
				Platform = Platform,
				ProjectFile = ProjectFile,
				OutputPath = EnsureRootedPath(ProjectFile, OutputPath),
				TargetFramework = TargetFramework,
				VisualStudioVersion = VisualStudioVersion,
				TargetFrameworkRootPath = TargetFrameworkRootPath,
				BinLogOutputPath = EnsureRootedPath(ProjectFile, BinLogOutputPath),
				BinLogEnabled = BinLogEnabled,
				MSBuildBinPath = Path.GetDirectoryName(new Uri(typeof(Microsoft.Build.Logging.ConsoleLogger).Assembly.CodeBase).LocalPath),
				AdditionalAssemblies = AdditionalAssemblies,
				SourceGenerators = SourceGenerators
			};

		private string EnsureRootedPath(string projectFile, string targetPath) =>
			Path.IsPathRooted(targetPath)
			? targetPath
			: Path.Combine(Path.GetDirectoryName(projectFile), targetPath);
	}
}
