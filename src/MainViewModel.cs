﻿using EnvDTE;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Shell;
using Document = Microsoft.CodeAnalysis.Document;
using Project = EnvDTE.Project;
using Task = System.Threading.Tasks.Task;

namespace Disasmo
{
    public class MainViewModel : ViewModelBase
    {
        private string _output;
        private string _loadingStatus;
        private bool _isLoading;
        private ISymbol _currentSymbol;
        private Document _codeDocument;
        private bool _success;
        private bool _tieredJitEnabled;
        private string _currentProjectOutputPath;
        private string _currentProjectPath;

        private static string DisasmoBeginMarker = "/*disasmo{*/";
        private static string DisasmoEndMarker = "/*}disasmo*/";

        public SettingsViewModel SettingsVm { get; } = new SettingsViewModel();

        public string Output
        {
            get => _output;
            set => Set(ref _output, value);
        }

        public string LoadingStatus
        {
            get => _loadingStatus;
            set => Set(ref _loadingStatus, value);
        }

        public bool Success
        {
            get => _success;
            set => Set(ref _success, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => Set(ref _isLoading, value);
        }

        // tier0, see https://github.com/dotnet/coreclr/issues/22123#issuecomment-458661007
        public bool TieredJitEnabled
        {
            get => _tieredJitEnabled;
            set
            {
                Set(ref _tieredJitEnabled, value);
                if (Success) RunFinalExe();
            }
        }

        public ICommand RefreshCommand => new RelayCommand(() => DisasmAsync(_currentSymbol, _codeDocument));

        private static async Task<(Location, bool)> GetEntryPointLocation(Document codeDoc, ISymbol currentSymbol)
        {
            try
            {
                Compilation compilation = await codeDoc.Project.GetCompilationAsync();
                IMethodSymbol entryPoint = compilation.GetEntryPoint(default(CancellationToken));
                if (entryPoint.Equals(currentSymbol))
                    return (null, true);
                Location location = entryPoint.Locations.FirstOrDefault();
                return (location, false);
            }
            catch
            {
                return (null, false);
            }
        }

        public async Task RunFinalExe()
        {
            try
            {
                Success = false;
                IsLoading = true;
                LoadingStatus = "Loading...";

                string exeRelativePath = $@"win-x64\publish\{Path.GetFileNameWithoutExtension(_currentProjectPath)}.exe";
                string finalExe = Path.Combine(Path.GetDirectoryName(_currentProjectPath), _currentProjectOutputPath, exeRelativePath);


                if (SettingsVm.UseBdnDisasm)
                {
                    var bdnResult = await BdnDisassembler.Disasm(finalExe, $"{_currentSymbol.ContainingType.ContainingNamespace.Name}.{_currentSymbol.ContainingType.Name}", _currentSymbol.Name);
                    if (bdnResult.Errors?.Length > 0)
                    {
                        Output = string.Join("\n", bdnResult.Errors);
                        return;
                    }
                    var bdnMethod = bdnResult?.Methods?.FirstOrDefault();
                    if (bdnMethod == null)
                    {
                        Output = "Something went wrong :(";
                        return;
                    }

                    string asm = "";
                    foreach (var map in bdnMethod.Maps)
                    {
                        foreach (var instruction in map.Instructions)
                        {
                            if (string.IsNullOrEmpty(instruction.Comment))
                                asm += instruction.Comment;
                            asm += instruction.TextRepresentation + "\n";
                        }
                    }

                    Output = asm;
                    Success = true;
                    return;
                }

                // see https://github.com/dotnet/coreclr/blob/master/Documentation/building/viewing-jit-dumps.md#specifying-method-names
                string target;

                if (_currentSymbol is IMethodSymbol)
                    target = _currentSymbol.ContainingType.Name + "::" + _currentSymbol.Name;
                else
                    target = _currentSymbol.Name + "::*";

                // TODO: it'll fail if the project has a custom assembly name (AssemblyName)

                LoadingStatus = "Executing: " + exeRelativePath;

                var envVars = new Dictionary<string, string>();
                envVars["COMPlus_TieredCompilation"] = TieredJitEnabled ? "1" : "0";

                if (SettingsVm.JitDumpInsteadOfDisasm)
                    envVars["COMPlus_JitDump"] = target;
                else
                    envVars["COMPlus_JitDisasm"] = target;
                SettingsVm.FillWithUserVars(envVars);

                var result = await ProcessUtils.RunProcess(finalExe, "", envVars);
                if (string.IsNullOrEmpty(result.Error))
                {
                    Success = true;
                    Output = PreprocessOutput(result.Output);
                }
                else
                {
                    Output = result.Error;
                }
            }
            catch (Exception e)
            {
                Output = e.ToString();
            }
            finally
            {
                IsLoading = false;
            }
        }

        private string PreprocessOutput(string output)
        {
            if (SettingsVm.ShowAsmComments && SettingsVm.ShowPrologueEpilogue)
                return output;

            var allLines = output.Split(new [] {"\n"}, StringSplitOptions.RemoveEmptyEntries);
            if (!SettingsVm.ShowAsmComments)
            {
                allLines = allLines.Where(l => !l.TrimStart().StartsWith(";")).SkipWhile(string.IsNullOrWhiteSpace).ToArray();
            }

            //TODO: remove Prologue&Epilogue

            return string.Join("\n", allLines);
        }

        public async void DisasmAsync(ISymbol symbol, Document codeDoc)
        {
            string entryPointFilePath = "";

            try
            {
                Success = false;
                IsLoading = true;
                _currentSymbol = symbol;
                _codeDocument = codeDoc;
                Output = "";

                if (symbol == null || codeDoc == null)
                    return;

                if (string.IsNullOrWhiteSpace(SettingsVm.PathToLocalCoreClr) && !SettingsVm.UseBdnDisasm)
                {
                    Output = "Path to a local CoreCLR is not set yet ^. (e.g. C:/prj/coreclr-master)\nPlease clone it and build it in both Release and Debug modes:\n\ncd coreclr-master\nbuild release skiptests\nbuild debug skiptests\n\nFor more details visit https://github.com/dotnet/coreclr/blob/master/Documentation/building/viewing-jit-dumps.md#setting-up-our-environment";
                    return;
                }

                if (SettingsVm.UseBdnDisasm && !(symbol is IMethodSymbol))
                {
                    Output = "BDN-disasm doesn't support classes, only methods.";
                    return;
                }

                if (symbol is IMethodSymbol method && method.IsGenericMethod)
                {
                    // TODO: ask user to specify type parameters
                    Output = "Generic methods are not supported yet.";
                    return;
                }

                // Find Release-x64 configuration:
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
                Project currentProject = dte.GetActiveProject();

                var allReleaseCfgs = currentProject.ConfigurationManager.OfType<Configuration>().Where(c => c.ConfigurationName == "Release").ToList();
                var neededConfig = allReleaseCfgs.FirstOrDefault(c => c.PlatformName?.Contains("64") == true);
                if (neededConfig == null)
                {
                    neededConfig = allReleaseCfgs.FirstOrDefault(c => c.PlatformName?.Contains("Any") == true);
                    if (neededConfig == null)
                    {
                        Output = "Couldn't find any 'Release - x64' or 'Release - Any CPU' configuration.";
                        return;
                    }
                }

                _currentProjectOutputPath = neededConfig.GetPropertyValueSafe("OutputPath");

                _currentProjectPath = currentProject.FileName;

                // TODO: validate TargetFramework, OutputType and AssemblyName properties
                // unfortunately both old VS API and new crashes for me on my vs2019preview2 (see https://github.com/dotnet/project-system/issues/669 and the workaround - both crash)
                // ugly hack for OutputType:
                if (!File.ReadAllText(_currentProjectPath).ToLower().Contains("<outputtype>exe<"))
                {
                    Output = "At this moment only .NET Core Сonsole Applications (`<OutputType>Exe</OutputType>`) are supported.\nFeel free to contribute multi-project support :-)";
                    return;
                }

                string currentProjectDirPath = Path.GetDirectoryName(_currentProjectPath);

                // first of all we need to restore packages if they are not restored
                // and do 'dotnet publish'
                // Basically, it follows https://github.com/dotnet/coreclr/blob/master/Documentation/building/viewing-jit-dumps.md
                // TODO: incremental update

                var (location, isMain) = await GetEntryPointLocation(_codeDocument, symbol);

                if (isMain)
                {
                    Output = "Sorry, but disasm for EntryPoints (Main()) is disabled.";
                    return;
                }

                entryPointFilePath = location?.SourceTree?.FilePath;
                if (string.IsNullOrEmpty(entryPointFilePath))
                {
                    Output = "Can't find Main() method in the project. (in order to inject 'RuntimeHelpers.PrepareMethod')";
                    return;
                }

                InjectPrepareMethod(entryPointFilePath, location.SourceSpan.Start, symbol, SettingsVm.UseBdnDisasm);

                if (!SettingsVm.SkipDotnetRestoreStep)
                {
                    LoadingStatus = "dotnet restore -r win-x64";
                    var restoreResult = await ProcessUtils.RunProcess("dotnet", "restore -r win-x64", null, currentProjectDirPath);
                    if (!string.IsNullOrEmpty(restoreResult.Error))
                    {
                        Output = restoreResult.Error;
                        return;
                    }
                }

                LoadingStatus = "dotnet publish -r win-x64 -c Release";
                var publishResult = await ProcessUtils.RunProcess("dotnet", "publish -r win-x64 -c Release", null, currentProjectDirPath);
                if (!string.IsNullOrEmpty(publishResult.Error))
                {
                    Output = publishResult.Error;
                    return;
                }

                // in case if there are compilation errors:
                if (publishResult.Output.Contains(": error"))
                {
                    Output = publishResult.Output;
                    return;
                }

                if (!SettingsVm.UseBdnDisasm)
                { 
                    LoadingStatus = "Copying files from locally built CoreCLR";
                    var dst = Path.Combine(currentProjectDirPath, _currentProjectOutputPath, @"win-x64\publish");
                    if (!Directory.Exists(dst))
                    {
                        Output = $"Something went wrong, {dst} doesn't exist after 'dotnet publish'";
                        return;
                    }

                    var clrReleaseFiles = Path.Combine(SettingsVm.PathToLocalCoreClr, @"bin\Product\Windows_NT.x64.Release");

                    if (!Directory.Exists(clrReleaseFiles))
                    {
                        Output = $"Folder + {clrReleaseFiles} does not exist. Please follow instructions at\n https://github.com/dotnet/coreclr/blob/master/Documentation/building/viewing-jit-dumps.md";
                        return;
                    }

                    var copyClrReleaseResult = await ProcessUtils.RunProcess("robocopy", $"/e \"{clrReleaseFiles}\" \"{dst}", null);
                    if (!string.IsNullOrEmpty(copyClrReleaseResult.Error))
                    {
                        Output = copyClrReleaseResult.Error;
                        return;
                    }

                    var clrJitFile = Path.Combine(SettingsVm.PathToLocalCoreClr, @"bin\Product\Windows_NT.x64.Debug\clrjit.dll");
                    if (!File.Exists(clrJitFile))
                    {
                        Output = $"File + {clrJitFile} does not exist. Please follow instructions at\n https://github.com/dotnet/coreclr/blob/master/Documentation/building/viewing-jit-dumps.md";
                        return;
                    }

                    File.Copy(clrJitFile, Path.Combine(dst, "clrjit.dll"), true);
                }
                await RunFinalExe();
            }
            catch (Exception e)
            {
                Output = e.ToString();
            }
            finally
            {
                RemoveInjectedPrepareMethodFromMain(entryPointFilePath);
                IsLoading = false;
            }
        }

        private static void InjectPrepareMethod(string mainPath, int mainStartIndex, ISymbol symbol, bool insertBigSleep)
        {
            // Did you expect to see some Roslyn magic here? :)
        
            string code = File.ReadAllText(mainPath);
            int indexOfMain = code.IndexOf('{', mainStartIndex) + 1;

            string template = DisasmoBeginMarker + "System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(typeof(%typename%).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic), w => w.DeclaringType == typeof(%typename%))).ForEach(m => System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle));System.Threading.Thread.Sleep(%sleep%);System.Environment.Exit(0);" + DisasmoEndMarker;

            string hostType = "global::" + symbol.ContainingNamespace + ".";
            if (symbol is IMethodSymbol)
                hostType += symbol.ContainingType.Name;
            else
                hostType += symbol.Name;

            code = code.Insert(indexOfMain, template
                    .Replace("%typename%", hostType)
                    .Replace("%sleep%", insertBigSleep ? "100000" : "10"));

            File.WriteAllText(mainPath, code);
        }

        private static void RemoveInjectedPrepareMethodFromMain(string mainPath)
        {
            try
            {
                if (string.IsNullOrEmpty(mainPath))
                    return;

                bool changed = false;
                var source = File.ReadAllText(mainPath);
                while (true)
                {
                    if (source.Contains(DisasmoBeginMarker))
                    {
                        var indexBegin = source.IndexOf(DisasmoBeginMarker, StringComparison.InvariantCulture);
                        var indexEnd = source.IndexOf(DisasmoEndMarker, indexBegin, StringComparison.InvariantCulture);
                        if (indexBegin >= 0 && indexEnd > indexBegin)
                        {
                            source = source.Remove(indexBegin, indexEnd - indexBegin + DisasmoEndMarker.Length);
                            changed = true;
                        }
                        else break;
                    }
                    else break;
                }

                if (changed)
                    File.WriteAllText(mainPath, source);
            }
            catch (Exception e)
            {
            }
        }
    }
}