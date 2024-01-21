using System.CommandLine;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TKMM.SarcTool.Common;
using TKMM.SarcTool.Plugins;
using TKMM.SarcTool.Services;

namespace TKMM.SarcTool;

public static class Program {

    public static int Main(string[] args) {

        PrintBanner();
        
        var rootCommand = GetCommandLine();
        return rootCommand.Invoke(args);
    }

    private static RootCommand GetCommandLine() {
        var mergeCommand = new Command("merge");
        var packageCommand = new Command("package");
        

        var verboseOption = new Option<bool>("--verbose", "Enable verbose output");

        MakeMergeCommand(mergeCommand, verboseOption);
        MakePackageCommand(packageCommand, verboseOption);
        
        var pluginCommand = new Command("showplugins");
        pluginCommand.SetHandler(() => ShowPlugins());
        
        var rootCommand = new RootCommand();
        rootCommand.Add(packageCommand);
        rootCommand.Add(mergeCommand);
        rootCommand.Add(pluginCommand);
        rootCommand.AddGlobalOption(verboseOption);
        
        

        
        return rootCommand;
    }

    private static void MakePackageCommand(Command packageCommand, Option<bool> verboseOption) {
        var packageCommandModOption = new Option<string>("--mod", "Path to the mod to perform the packaging on") {
                IsRequired = true
            }
            .LegalFilePathsOnly();

        var packageCommandOutputOption = new Option<string>("--output", "Merged mods output directory") {
                IsRequired = true
            }
            .LegalFilePathsOnly();

        var packageCommandVersionsOption = new Option<string[]>("--versions", "Versions to try and package against");
        packageCommandVersionsOption.SetDefaultValue(new[] {"100", "110", "111", "120", "121"});
        packageCommandVersionsOption.AddValidator(val => {
            if (!val.Tokens.All(l => Int32.TryParse(l.Value, out _)))
                val.ErrorMessage = "Specified versions must be a number.";
        });

        var packageCommandConfigOption = new Option<string?>(
                "--config",
                "Path to the TKMM configuration files (config.json). Default if not specified.")
            .LegalFileNamesOnly();

        var packageCommandChecksumOption = new Option<string?>(
                "--checksum",
                "Path to the TKMM checksum database. Default if not specified."
            )
            .LegalFileNamesOnly();

        packageCommand.AddOption(packageCommandModOption);
        packageCommand.AddOption(packageCommandOutputOption);
        packageCommand.AddOption(packageCommandConfigOption);
        packageCommand.AddOption(packageCommandChecksumOption);
        packageCommand.AddOption(packageCommandVersionsOption);

        packageCommand.SetHandler((outputPath, modPath, configPath, checksumPath, versions, verbose) =>
                                      RunPackage(outputPath, modPath, configPath, checksumPath, versions, verbose),
                                  packageCommandOutputOption,
                                  packageCommandModOption, packageCommandConfigOption, packageCommandChecksumOption,
                                  packageCommandVersionsOption, verboseOption);
    }

    private static void MakeMergeCommand(Command mergeCommand, Option<bool> verboseOption) {
        var mergeCommandModsOption = new Option<IEnumerable<string>>(
            "--mods", "A list of mod folder names, within the base mod folder, to merge, in order of priority") {
            IsRequired = true,
            AllowMultipleArgumentsPerToken = true,
        };

        var mergeCommandBaseOption = new Option<string>("--base", "The base folder path containing the mod subfolders") {
                IsRequired = true
            }
            .LegalFilePathsOnly();

        var mergeConfigOption =
            new Option<string?>(
                "--config", "Path to the TKMM configuration files (config.json, shops.json). Default if not specified.");

        var mergeCommandOutputOption = new Option<string>("--output", "Merged mods output directory") {
                IsRequired = true
            }
            .LegalFilePathsOnly();

        var mergeProcessModeOption = new Option<ProcessMode>("--process", "Specify what type of merge to perform");

        mergeCommand.AddOption(mergeCommandBaseOption);
        mergeCommand.AddOption(mergeCommandModsOption);
        mergeCommand.AddOption(mergeCommandOutputOption);
        mergeCommand.AddOption(mergeConfigOption);
        mergeCommand.AddOption(mergeProcessModeOption);

        mergeCommand.SetHandler((modsList, basePath, outputPath, configPath, verbose, processMode) =>
                                    RunMerge(modsList, basePath, outputPath, configPath, verbose, processMode),
                                mergeCommandModsOption, mergeCommandBaseOption, mergeCommandOutputOption,
                                mergeConfigOption, verboseOption, mergeProcessModeOption);
    }

    private static IHost Initialize() {
        var hostBuilder = new HostApplicationBuilder(new HostApplicationBuilderSettings() {
            DisableDefaults = true
        });
        
        // Plugins
        var pluginManager = new PluginManager();
        hostBuilder.Services.AddSingleton<IPluginManager>(pluginManager);
        pluginManager.LoadPlugins(hostBuilder.Services);
        
        // Base services
        var globals = new Globals();
        hostBuilder.Services.AddSingleton<IGlobals>(globals);
        hostBuilder.Services.AddTransient<IHandlerManager, HandlerManager>();
        hostBuilder.Services.AddTransient<MergeService>();
        hostBuilder.Services.AddTransient<PackageService>();
        hostBuilder.Services.AddTransient<ConfigService>();
        hostBuilder.Services.AddTransient<ILogger, SpectreConsoleLogger>();
        
        // Logging
        hostBuilder.Logging.ClearProviders();

        return hostBuilder.Build();

    }

    private static int RunMerge(IEnumerable<string> modsList, string basePath, string outputPath, string? configPath,
                                bool verbose, ProcessMode processMode) {
        try {
            var host = Initialize();
            var mergeService = host.Services.GetRequiredService<MergeService>();
            var globals = host.Services.GetRequiredService<IGlobals>();
            
            // Set global verbosity
            (globals as Globals)!.Verbose = verbose;

            // Reverse the order of the mods because we need to process the lowest priority mod first
            var modsToMerge = modsList.ToArray().Reverse();

            int result = 0;
            if (processMode == ProcessMode.All) {
                result = mergeService.ExecuteArchiveMerge(modsToMerge, basePath, outputPath, configPath);
                
                if (result == 0)
                    result = mergeService.ExecuteFlatMerge(modsToMerge, basePath, outputPath, configPath);
            } else if (processMode == ProcessMode.Archive) {
                result = mergeService.ExecuteArchiveMerge(modsToMerge, basePath, outputPath, configPath);
            } else if (processMode == ProcessMode.Flat) {
                result = mergeService.ExecuteFlatMerge(modsToMerge, basePath, outputPath, configPath);
            }
            
            return result;
        } catch (Exception exc) {
            AnsiConsole.WriteException(exc, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
            return -1;
        }
    }

    private static int RunPackage(string outputPath, string modPath, string? configPath, string? checksumPath,
                                  string[] versions, bool verbose) {
        
        try {
            var host = Initialize();
            var packageService = host.Services.GetRequiredService<PackageService>();
            var globals = host.Services.GetRequiredService<IGlobals>();

            // Set global verbosity
            (globals as Globals)!.Verbose = verbose;

            var result = packageService.Execute(outputPath, modPath, configPath, checksumPath, versions);
            return result;
        } catch (Exception exc) {
            AnsiConsole.WriteException(exc, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
            return -1;
        }
    }

    private static int ShowPlugins() {
        try {
            var host = Initialize();
            var pluginManager = host.Services.GetRequiredService<IPluginManager>();

            var table = new Table();
            table.AddColumns("Plugin", "Formats");

            if (!pluginManager.GetPlugins().Any()) {
                AnsiConsole.Markup("[red]No plugins found. Cannot handle any formats.[/]");
                return 0;
            }
            
            foreach (var plugin in pluginManager.GetPlugins()) {
                table.AddRow(plugin.Name, String.Join(", ", plugin.Extensions));
            }

            AnsiConsole.Write(table);
            return 0;
        } catch (Exception exc) {
            AnsiConsole.WriteException(exc);
            return -1;
        }
    }
    
    private static void PrintBanner() {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = $"{assembly.GetName().Version?.Major}.{assembly.GetName().Version?.Minor}";
        
        AnsiConsole.Write(new FigletText("SARC Tool"));
        AnsiConsole.MarkupInterpolated(
            $"[bold]TKMM SARC Tool v. {version}\nhttps://github.com/okmika/TKMM-SARC[/]\n\n");
        
    }

    
    
}

public enum OperationMode {
    Package,
    Merge
}

public enum ProcessMode {
    All,
    Archive,
    Flat
}