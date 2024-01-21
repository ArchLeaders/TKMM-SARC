using Microsoft.Extensions.Logging;
using SarcLibrary;
using Spectre.Console;
using TKMM.SarcTool.Common;
using TKMM.SarcTool.Compression;
using TKMM.SarcTool.Special;

namespace TKMM.SarcTool.Services;

internal class MergeService {

    private readonly ConfigService configService;
    private readonly IHandlerManager handlerManager;

    private ConfigJson? config;
    private ZsCompression? compression;
    private bool verboseOutput;
    private List<ShopsJsonEntry>? shops;

    private readonly string[] supportedExtensions = new[] {
        ".bars", ".bfarc", ".bkres", ".blarc", ".genvb", ".pack", ".ta",
        ".bars.zs", ".bfarc.zs", ".bkres.zs", ".blarc.zs", ".genvb.zs", ".pack.zs", ".ta.zs"
    };

    public MergeService(ConfigService configService, IHandlerManager handlerManager, IGlobals globals) {
        this.configService = configService;
        this.handlerManager = handlerManager;
        this.verboseOutput = globals.Verbose;
    }

    public int ExecuteArchiveMerge(IEnumerable<string> modsList, string basePath, string outputPath, string? configPath) {

        if (String.IsNullOrWhiteSpace(configPath))
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "Totk");

        if (!File.Exists(Path.Combine(configPath, "config.json"))) {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not find config.json in {configPath}\n[bold]Abort.[/][/]");
            return -1;
        }

        if (!Initialize(configPath))
            return -1;
        
        InternalMergeArchives(modsList.ToArray(), basePath, outputPath);

        AnsiConsole.MarkupLine("[green][bold]Merging completed successfully.[/][/]");
        return 0;

    }

    public int ExecuteFlatMerge(IEnumerable<string> modsList, string basePath, string outputPath, string? configPath) {
        if (String.IsNullOrWhiteSpace(configPath))
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "Totk");

        if (!File.Exists(Path.Combine(configPath, "config.json"))) {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not find config.json in {configPath}\n[bold]Abort.[/][/]");
            return -1;
        }

        if (!Initialize(configPath))
            return -1;
        
        InternalFlatMerge(modsList.ToArray(), basePath, outputPath);

        AnsiConsole.MarkupLine("[green][bold]Merging completed successfully.[/][/]");
        return 0;
    }

    private void InternalFlatMerge(string[] modsList, string basePath, string outputPath) {
        AnsiConsole.MarkupLineInterpolated($"[bold]Merging flat files in mods {String.Join(", ", modsList.Select(l => $"'{l}'"))} into \"{outputPath}\"[/]");

        AnsiConsole.Status()
                   .Spinner(Spinner.Known.Dots2)
                   .Start("Preparing...", context => {
                       foreach (var modFolderName in modsList) {
                           context.Status($"Processing {modFolderName}...");
                           MergeFilesInMod(modFolderName, basePath, outputPath, context);
                       }

                       context.Status($"Merging shops...");
                   });
    }

    private void InternalMergeArchives(string[] modsList, string basePath, string outputPath) {

        AnsiConsole.MarkupLineInterpolated($"[bold]Merging mods {String.Join(", ", modsList.Select(l => $"'{l}'"))} into \"{outputPath}\"[/]");

        AnsiConsole.Status()
                   .Spinner(Spinner.Known.Dots2)
                   .Start("Preparing...", context => {
                       CleanPackagesInTarget(outputPath);
                       
                       foreach (var modFolderName in modsList) {
                           context.Status($"Processing {modFolderName}...");
                           MergeArchivesInMod(modFolderName, basePath, outputPath, context);
                       }

                       MergeShops(outputPath, context);
                   });


    }

    private void MergeFilesInMod(string modFolderName, string basePath, string outputPath,
                                 StatusContext statusContext) {

        // Get archive files in mod folder
        var modFolderPath = Path.Combine(basePath, modFolderName);

        if (!Directory.Exists(modFolderPath))
            throw new Exception($"Mod folder '{modFolderName}' does not exist under '{basePath}'");

        var filesInModFolder =
            Directory.GetFiles(modFolderPath, "*", SearchOption.AllDirectories);

        var supportedFlatExtensions = handlerManager.GetSupportedExtensions().ToHashSet();
        supportedFlatExtensions = supportedFlatExtensions.Concat(supportedFlatExtensions.Select(l => $"{l}.zs")).ToHashSet();

        foreach (var filePath in filesInModFolder) {
            var extension = Path.GetExtension(filePath).Substring(1).ToLower();

            if (!supportedFlatExtensions.Contains(extension))
                continue;

            var baseRomfs = Path.Combine(basePath, modFolderName, "romfs");
            var pathRelativeToBase = Path.GetRelativePath(baseRomfs, Path.GetDirectoryName(filePath)!);

            MergeFile(filePath, modFolderName, pathRelativeToBase, outputPath);

            AnsiConsole.MarkupLineInterpolated($"» [green]Merged {filePath} into {pathRelativeToBase}");
        }
    }

    private void MergeShops(string outputPath, StatusContext context) {
        if (shops == null || shops.Count == 0) {
            AnsiConsole.MarkupLineInterpolated($"! [yellow]Shops definition is empty. Skipping overflow merge.[/]");
            return;
        }
        
        var merger = new ShopsMerger(this, shops.Select(l => l.ActorName).ToHashSet(), verboseOutput);
        
        // This will be called if we ever need to request a shop file from the dump
        merger.GetEntryForShop = (actorName) => {
            var dumpPath = Path.Combine(config!.GamePath!, "Pack", "Actor", $"{actorName}.pack.zs");
            var target = Path.Combine(outputPath, "Pack", "Actor", $"{actorName}.pack.zs");

            File.Copy(dumpPath, target);

            return new ShopsMerger.ShopMergerEntry(actorName, target);
        };
        
        foreach (var shop in shops) {
            context.Status($"Preparing to merge shops for {shop.ActorName}...");

            var archivePath = Path.Combine(outputPath, "Pack", "Actor", $"{shop.ActorName}.pack.zs");
            if (!File.Exists(archivePath)) {
                if (verboseOutput)
                    AnsiConsole.MarkupLineInterpolated($"- Skipping shop {shop.ActorName}");

                continue;
            }

            merger.Add(shop.ActorName, archivePath);
        }

        merger.MergeShops(context);

        AnsiConsole.MarkupLineInterpolated($"» [green]Merged shops successfully.[/]");
    }

    private void MergeFile(string filePath, string modFolderName, string pathRelativeToBase, string outputPath) {
        var targetFilePath = Path.Combine(outputPath, pathRelativeToBase, Path.GetFileName(filePath));

        // If the output doesn't even exist just copy it over and we're done
        if (!File.Exists(targetFilePath)) {
            if (!Directory.Exists(Path.GetDirectoryName(targetFilePath)))
                Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);

            File.Copy(filePath, targetFilePath);
            return;
        }

        // Otherwise try to reconcile and merge
        var isCompressed = filePath.EndsWith(".zs");

        var sourceFileContents = GetFlatFileContents(filePath, isCompressed);
        var targetFileContents = GetFlatFileContents(targetFilePath, isCompressed);

        var fileExtension = Path.GetExtension(filePath).Substring(1).ToLower();
        var handler = handlerManager.GetHandlerInstance(fileExtension);

        if (handler == null) {
            if (verboseOutput)
                AnsiConsole.MarkupLineInterpolated($"! [yellow]{modFolderName}: No handler for type {fileExtension}, overwriting {Path.GetFileName(filePath)} in {pathRelativeToBase}[/]");

            File.Copy(filePath, targetFilePath);
        } else {
            var relativeFilename = Path.Combine(pathRelativeToBase, Path.GetFileName(filePath));

            if (Path.DirectorySeparatorChar != '/')
                relativeFilename = relativeFilename.Replace(Path.DirectorySeparatorChar, '/');
            
            var result = handler.Merge(relativeFilename, new List<MergeFile>() {
                new MergeFile(1, sourceFileContents),
                new MergeFile(0, targetFileContents)
            });

            WriteFlatFileContents(targetFilePath, result, isCompressed);
        }
    }

    private void MergeArchivesInMod(string modFolderName, string basePath, string outputPath, StatusContext statusContext) {
        
        // Get archive files in mod folder
        var modFolderPath = Path.Combine(basePath, modFolderName);

        if (!Directory.Exists(modFolderPath))
            throw new Exception($"Mod folder '{modFolderName}' does not exist under '{basePath}'");
        
        var filesInModFolder = Directory.GetFiles(modFolderPath, "*", SearchOption.AllDirectories);

        foreach (var filePath in filesInModFolder.Where(file => supportedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))) {

            var baseRomfs = Path.Combine(basePath, modFolderName, "romfs");
            var pathRelativeToBase = Path.GetRelativePath(baseRomfs, Path.GetDirectoryName(filePath)!);
            statusContext.Status($"Merging {filePath}...");
            
            MergeArchive(modFolderName, filePath, pathRelativeToBase, outputPath);

            AnsiConsole.MarkupLineInterpolated($"» [green]Merged {modFolderName} into {pathRelativeToBase}[/]");
        }
        
    }

    private void CleanPackagesInTarget(string outputPath) {
        if (!Directory.Exists(outputPath))
            return;
        
        AnsiConsole.MarkupLineInterpolated($"! [yellow]Cleaning existing archives in output {outputPath}[/]");

        var filesInOutputFolder = Directory.GetFiles(outputPath, "*", SearchOption.AllDirectories);
        foreach (var filePath in filesInOutputFolder.Where(file => supportedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))) {
            if (verboseOutput)
                AnsiConsole.MarkupLineInterpolated($"   -> [yellow]Deleting {filePath}[/]");
            
            File.Delete(filePath);
        }
    }

    private void MergeArchive(string modFolderName, string archivePath, string pathRelativeToBase, string outputPath) {
        // If the output doesn't even exist just copy it over and we're done
        var targetArchivePath = Path.Combine(outputPath, pathRelativeToBase, Path.GetFileName(archivePath));

        if (!File.Exists(targetArchivePath)) {
            if (!Directory.Exists(Path.GetDirectoryName(targetArchivePath)))
                Directory.CreateDirectory(Path.GetDirectoryName(targetArchivePath)!);

            CopyOriginal(archivePath, pathRelativeToBase, targetArchivePath);
        }
        
        // Otherwise try to reconcile and merge
        var isCompressed = archivePath.EndsWith(".zs");
        var isPackFile = archivePath.Contains(".pack.");

        Span<byte> sourceFileContents = GetFileContents(archivePath, isCompressed, isPackFile);
        Span<byte> targetFileContents = GetFileContents(targetArchivePath, isCompressed, isPackFile);

        var sourceSarc = Sarc.FromBinary(sourceFileContents);
        var targetSarc = Sarc.FromBinary(targetFileContents);

        foreach (var entry in sourceSarc) {
            if (!targetSarc.ContainsKey(entry.Key)) {
                // If the archive doesn't have the file, add it
                targetSarc.Add(entry.Key, entry.Value);
            } else {
                // Otherwise, reconcile with the handler
                var fileExtension = Path.GetExtension(entry.Key).Substring(1);
                var handler = handlerManager.GetHandlerInstance(fileExtension);

                if (handler == null) {
                    if (verboseOutput)
                        AnsiConsole.MarkupLineInterpolated($"! [yellow]{modFolderName}: No handler for type {fileExtension}, overwriting {entry.Key} in {targetArchivePath}[/]");
                    
                    targetSarc[entry.Key] = entry.Value;
                    continue;
                }

                if (verboseOutput)
                    AnsiConsole.MarkupLineInterpolated($"- {modFolderName}: Merging {entry.Key} into archive {targetArchivePath}");
                
                var result = handler.Merge(entry.Key, new List<MergeFile>() {
                    new MergeFile(1, entry.Value),
                    new MergeFile(0, targetSarc[entry.Key])
                });

                targetSarc[entry.Key] = result.ToArray();
            }
        }

        WriteFileContents(targetArchivePath, targetSarc, isCompressed, isPackFile);
    }

    internal void WriteFileContents(string archivePath, Sarc sarc, bool isCompressed, bool isPackFile) {
        if (compression == null)
            throw new Exception("Compression not loaded");

        using var memoryStream = new MemoryStream();
        sarc.Write(memoryStream);

        if (isCompressed) {
            File.WriteAllBytes(archivePath,
                               compression.Compress(memoryStream.ToArray(),
                                                    isPackFile ? CompressionType.Pack : CompressionType.Common)
                                          .ToArray());
        } else {
            File.WriteAllBytes(archivePath, memoryStream.ToArray());
        }
    }

    internal Span<byte> GetFileContents(string archivePath, bool isCompressed, bool isPackFile) {
        if (compression == null)
            throw new Exception("Compression not loaded");
        
        Span<byte> sourceFileContents;
        if (isCompressed) {
            // Need to decompress the file first
            var compressedContents = File.ReadAllBytes(archivePath).AsSpan();
            sourceFileContents = compression.Decompress(compressedContents,
                                                        isPackFile ? CompressionType.Pack : CompressionType.Common);
        } else {
            sourceFileContents = File.ReadAllBytes(archivePath).AsSpan();
        }

        return sourceFileContents;
    }

    private Memory<byte> GetFlatFileContents(string filePath, bool isCompressed) {
        if (compression == null)
            throw new Exception("Compression not loaded");

        Span<byte> sourceFileContents;
        if (isCompressed) {
            // Need to decompress the file first
            var compressedContents = File.ReadAllBytes(filePath).AsSpan();
            sourceFileContents = compression.Decompress(compressedContents, CompressionType.Common);
        } else {
            sourceFileContents = File.ReadAllBytes(filePath).AsSpan();
        }

        return new Memory<byte>(sourceFileContents.ToArray());
    }

    private void WriteFlatFileContents(string filePath, ReadOnlyMemory<byte> contents, bool isCompressed) {
        if (compression == null)
            throw new Exception("Compression not loaded");

        if (isCompressed) {
            File.WriteAllBytes(filePath,
                               compression.Compress(contents.ToArray(), CompressionType.Common).ToArray());
        } else {
            File.WriteAllBytes(filePath, contents.ToArray());
        }
    }

    private void CopyOriginal(string archivePath, string pathRelativeToBase, string outputFile) {
        var sourcePath = config!.GamePath!;
        var originalFile = Path.Combine(sourcePath, pathRelativeToBase, Path.GetFileName(archivePath));

        if (File.Exists(originalFile)) {
            if (verboseOutput)
                AnsiConsole.MarkupLineInterpolated($"! [yellow]Copying file {originalFile} to {outputFile}[/]");
            
            File.Copy(originalFile, outputFile);
        }
    }

    private bool Initialize(string configPath) {
        shops = configService.GetShops(Path.Combine(configPath, "shops.json"));

        if (shops.Count == 0)
            AnsiConsole.MarkupLineInterpolated($"! [yellow]{configPath} does not include shops.json or it's empty. Shops merging disabled.[/]");
        
        config = configService.GetConfig(Path.Combine(configPath, "config.json"));

        if (String.IsNullOrWhiteSpace(config.GamePath)) {
            AnsiConsole.MarkupInterpolated(
                $"[red]Config file does not include path to a dump of the game. [bold]Abort.[/][/]");
            return false;
        }

        // Try to init compression
        var compressionPath = Path.Combine(this.config.GamePath, "Pack", "ZsDic.pack.zs");
        if (!File.Exists(compressionPath)) {
            AnsiConsole.MarkupInterpolated($"[red]Could not find compression dictionary: {compressionPath
            }\n[bold]Abort.[/][/]");
            return false;
        }

        compression = new ZsCompression(compressionPath);
        return true;
    }
    
}