using System.Diagnostics;
using System.Text.Json;
using SarcLibrary;
using TKMM.SarcTool.Core.Model;
using TotkCommon;

namespace TKMM.SarcTool.Core;

/// <summary>
/// Merges changes to SARC archives, flat BYML files, and GameDataList files
/// as defined in changelogs generated by <see cref="SarcPackager"/>.
/// </summary>
public class SarcMerger {
    private readonly Totk config;
    private readonly List<ShopsJsonEntry> shops;

    private readonly string outputPath;
    private readonly string[] modFolderPaths;
    private readonly HandlerManager handlerManager;
    private readonly ArchiveHelper archiveHelper;

    /// <summary>
    /// Emit verbose trace events. Useful for debugging failures but may slow down operations. 
    /// </summary>
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Creates a new instance of the <see cref="SarcMerger"/> class.
    /// </summary>
    /// <param name="modFolderPaths">
    ///     A list full paths to the mods to merge, in the order of lowest to highest priority. Each of these
    ///     folders should be the path to the "romfs" folder of the mod.
    /// </param>
    /// <param name="outputPath">The full path to the location of the folder in which to place the final merged files.</param>
    /// <param name="configPath">
    ///     The path to the location of the "config.json" file in standard NX Toolbox format, or
    ///     null to use the default location in local app data.
    /// </param>
    /// <param name="shopsPath">
    ///     The full path to the "shops.json" file in TKMM format, for use by the shops merger, or
    ///     null to use the default location in local app data.
    /// </param>
    /// <exception cref="ArgumentNullException">
    ///     Thrown if any of the required parameters are null.
    /// </exception>
    /// <exception cref="Exception">
    ///     Thrown if any of the configuration files are not found, or if the compression
    ///     dictionary is missing.
    /// </exception>
    public SarcMerger(IEnumerable<string> modFolderPaths, string outputPath, string? configPath = null, string? shopsPath = null) {
        ArgumentNullException.ThrowIfNull(modFolderPaths);

        if (!outputPath.Contains($"{Path.DirectorySeparatorChar}romfs"))
            throw new ArgumentException("Path must be to the \"romfs\" folder of the output", nameof(outputPath));

        this.modFolderPaths = modFolderPaths.ToArray();

        // Verify that each mod folder has a romfs
        foreach (var folder in this.modFolderPaths) {
            if (!folder.EndsWith($"{Path.DirectorySeparatorChar}romfs"))
                throw new ArgumentException($"{folder} must be the \"romfs\" for the mod",
                                            nameof(modFolderPaths));
        }

        this.outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
        

        this.handlerManager = new HandlerManager();
        
        configPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Totk", "config.json");

        shopsPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tkmm",
                                     "shops.json");

        if (!File.Exists(configPath))
            throw new Exception($"{configPath} not found");

        using FileStream fs = File.OpenRead(configPath);
        this.config = JsonSerializer.Deserialize<Totk>(fs)
            ?? new();

        if (String.IsNullOrWhiteSpace(this.config.GamePath))
            throw new Exception("Game path is not defined in config.json");

        var compressionPath = Path.Combine(this.config.GamePath, "Pack", "ZsDic.pack.zs");
        if (!File.Exists(compressionPath)) {
            throw new Exception("Compression package not found: {this.config.GamePath}");
        }

        if (!File.Exists(shopsPath))
            throw new Exception($"{shopsPath} not found");

        this.shops = ShopsJsonEntry.Load(shopsPath);
        
        var compression = new ZsCompression(compressionPath);
        this.archiveHelper = new ArchiveHelper(compression);

    }

    /// <summary>
    /// Perform merging on the selected packages.
    /// </summary>
    public void Merge() {

        Trace.TraceInformation("Merging archives");
        InternalMergeArchives();

        Trace.TraceInformation("Merging flat files");
        InternalFlatMerge();

    }

    /// <summary>
    /// Perform merging on the selected packages asynchronously.
    /// </summary>
    /// <returns>A task that represents the merging work queued on the task pool.</returns>
    public async Task MergeAsync() {
        await Task.Run(Merge);
    }

    /// <summary>
    /// Evaluate the provided GameDataList (GDL) files and return whether
    /// there are any differences between the two. This is not a byte-for-byte
    /// compare, but rather a logical evaluation of the internal contents of the
    /// GDL files.
    /// </summary>
    /// <param name="fileOne">The full path to the first compressed GDL file to compare</param>
    /// <param name="fileTwo">The full path to the second compressed GDL file to compare</param>
    /// <returns>True if there are any differences between the GDL files</returns>
    /// <exception cref="Exception">
    ///     Thrown if any of the provided GDL files is not compressed.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    ///     Thrown if any of the required parameters are null.
    /// </exception>
    public bool HasGdlChanges(string fileOne, string fileTwo) {
        ArgumentNullException.ThrowIfNull(fileOne);
        ArgumentNullException.ThrowIfNull(fileTwo);

        if (!fileOne.EndsWith(".zs") || !fileTwo.EndsWith(".zs")) {
            throw new Exception("Only compressed (.zs) GDL files are supported");
        }

        var fileOneBytes = archiveHelper.GetFlatFileContents(fileOne, true, out _);
        var fileTwoBytes = archiveHelper.GetFlatFileContents(fileTwo, true, out _);

        var merger = new GameDataListMerger();
        return merger.Compare(fileOneBytes, fileTwoBytes);
    }

    /// <summary>
    /// Asynchronously evaluate the provided GameDataList (GDL) files and return whether
    /// there are any differences between the two. This is not a byte-for-byte
    /// compare, but rather a logical evaluation of the internal contents of the
    /// GDL files.
    /// </summary>
    /// <param name="fileOne">The full path to the first compressed GDL file to compare</param>
    /// <param name="fileTwo">The full path to the second compressed GDL file to compare</param>
    /// <returns>True if there are any differences between the GDL files</returns>
    /// <exception cref="Exception">
    ///     Thrown if any of the provided GDL files is not compressed.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    ///     Thrown if any of the required parameters are null.
    /// </exception>
    public async Task<bool> HasGdlChangesAsync(string fileOne, string fileTwo) {
        return await Task.Run(() => HasGdlChanges(fileOne, fileTwo));
    }

    private void InternalFlatMerge() {
        foreach (var modFolder in modFolderPaths) {
            TracePrint("Processing {0}", modFolder);
            
            MergeFilesInMod(modFolder);
            
            Trace.TraceInformation("Processing GDL in {0}", modFolder);
            MergeGameDataList(modFolder);
        }
    }

    private void InternalMergeArchives() {

        CleanPackagesInTarget();

        Trace.TraceInformation("Output path is {0}", outputPath);

        foreach (var modFolderName in modFolderPaths) {
            Trace.TraceInformation("Processing {0}", modFolderName);
            MergeArchivesInMod(modFolderName);
        }

        Trace.TraceInformation("Merging shops");
        MergeShops();

    }

    private void MergeFilesInMod(string modFolderPath) {

        if (!Directory.Exists(modFolderPath))
            throw new Exception($"The input mod folder '{modFolderPath}' could not be found.");

        var filesInModFolder =
            Directory.GetFiles(modFolderPath, "*", SearchOption.AllDirectories);

        var supportedFlatExtensions = handlerManager.GetSupportedExtensions().ToHashSet();
        supportedFlatExtensions = supportedFlatExtensions.Concat(supportedFlatExtensions.Select(l => $"{l}.zs")).ToHashSet();

        var folderExclusions = new[] {"RSDB"};
        var extensionExclusions = new[] {".rstbl.byml", ".rstbl.byml.zs"};
        var prefixExclusions = new[] {"GameDataList.Product"};

        Parallel.ForEach(filesInModFolder, filePath => {
            if (!supportedFlatExtensions.Any(l => filePath.EndsWith(l)))
                return;

            if (folderExclusions.Any(
                    l => filePath.Contains(Path.DirectorySeparatorChar + l + Path.DirectorySeparatorChar)))
                return;

            if (extensionExclusions.Any(l => filePath.EndsWith(l)))
                return;

            if (prefixExclusions.Any(l => Path.GetFileName(filePath).StartsWith(l)))
                return;
            
            var pathRelativeToBase = Path.GetRelativePath(modFolderPath, Path.GetDirectoryName(filePath)!);

            try {
                MergeFile(filePath, modFolderPath, pathRelativeToBase);
            } catch {
                Trace.TraceError("Failed to merge {0}", filePath);
                throw;
            }
        });
        
    }

    private void MergeGameDataList(string modPath) {
        var gdlChangelog = Path.Combine(modPath, "GameData", "GameDataList.gdlchangelog");

        if (!File.Exists(gdlChangelog))
            return;
        
        // Copy over vanilla files first
        var vanillaGdlPath = Path.Combine(config.GamePath, "GameData");

        if (!Directory.Exists(vanillaGdlPath))
            throw new Exception($"Failed to find vanilla GDL files at {vanillaGdlPath}");

        var vanillaGdlFiles = Directory.GetFiles(vanillaGdlPath)
                                       .Where(l => Path.GetFileName(l).StartsWith("GameDataList.Product") &&
                                                   Path.GetFileName(l).EndsWith(".byml.zs"));

        foreach (var vanillaFile in vanillaGdlFiles) {
            var outputGdl = Path.Combine(outputPath, "GameData", Path.GetFileName(vanillaFile));

            Directory.CreateDirectory(Path.GetDirectoryName(outputGdl)!);
            
            if (!File.Exists(outputGdl))
                CopyHelper.CopyFile(vanillaFile, outputGdl);
        }

        var gdlFiles = Directory.GetFiles(Path.Combine(outputPath, "GameData"))
                                .Where(l => Path.GetFileName(l).StartsWith("GameDataList.Product"))
                                .ToList();

        var changelogBytes = File.ReadAllBytes(gdlChangelog);

        foreach (var gdlFile in gdlFiles) {
            var gdlFileBytes = archiveHelper.GetFlatFileContents(gdlFile, true, out var dictionaryId);
            var merger = new GameDataListMerger();

            var resultBytes = merger.Merge(gdlFileBytes, changelogBytes);

            archiveHelper.WriteFlatFileContents(gdlFile, resultBytes, true, dictionaryId);

            Trace.TraceInformation("Merged GDL changelog into {0}", gdlFile);
        }

        // Delete the changelog in the output folder in case it's there
        var gdlChangelogInOutput = Path.Combine(outputPath, "GameData", "GameDataList.gdlchangelog");
        if (File.Exists(gdlChangelogInOutput))
            File.Delete(gdlChangelogInOutput);

    }

    private void MergeShops() {
      
        
        var merger = new ShopsMerger(archiveHelper, shops.Select(l => l.ActorName).ToHashSet(), Verbose);
        
        // This will be called if we ever need to request a shop file from the dump
        merger.GetEntryForShop = (actorName) => {
            var dumpPath = Path.Combine(config.GamePath, "Pack", "Actor", $"{actorName}.pack.zs");
            var target = Path.Combine(outputPath, "Pack", "Actor", $"{actorName}.pack.zs");

            CopyHelper.CopyFile(dumpPath, target);

            return new ShopsMerger.ShopMergerEntry(actorName, target);
        };
        
        foreach (var shop in shops) {
            var archivePath = Path.Combine(outputPath, "Pack", "Actor", $"{shop.ActorName}.pack.zs");
            if (!File.Exists(archivePath)) {
                continue;
            }

            merger.Add(shop.ActorName, archivePath);
        }

        merger.MergeShops();
    }

    private void MergeFile(string filePath, string modFolderName, string pathRelativeToBase) {
        var targetFilePath = Path.Combine(outputPath, pathRelativeToBase, Path.GetFileName(filePath));

        // If the output doesn't even exist just copy it over and we're done
        if (!File.Exists(targetFilePath)) {
            if (!Directory.Exists(Path.GetDirectoryName(targetFilePath)))
                Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);

            var didCopy = CopyOriginal(filePath, pathRelativeToBase, targetFilePath);

            // Copy the mod's file to the output if we otherwise failed to copy the file from the dump
            if (!didCopy) {
                CopyHelper.CopyFile(filePath, targetFilePath);
                return;
            }
        }

        // Otherwise try to reconcile and merge
        var sourceIsCompressed = filePath.EndsWith(".zs");
        var targetIsCompressed = filePath.EndsWith(".zs");

        var sourceFileContents = archiveHelper.GetFlatFileContents(filePath, sourceIsCompressed, out _);
        var targetFileContents = archiveHelper.GetFlatFileContents(targetFilePath, targetIsCompressed, out var dictionaryId);

        var fileExtension = Path.GetExtension(filePath.Replace(".zs", "")).Substring(1).ToLower();
        var handler = handlerManager.GetHandlerInstance(fileExtension);

        if (handler == null) {
            TracePrint("{0}: Wrote {1} in {2} by priority", modFolderName,
                               Path.GetFileName(filePath), pathRelativeToBase);
            
            CopyHelper.CopyFile(filePath, targetFilePath);
        } else {
            var relativeFilename = Path.Combine(pathRelativeToBase, Path.GetFileName(filePath));

            if (Path.DirectorySeparatorChar != '/')
                relativeFilename = relativeFilename.Replace(Path.DirectorySeparatorChar, '/');
            
            var result = handler.Merge(relativeFilename, new List<MergeFile>() {
                new MergeFile(1, sourceFileContents),
                new MergeFile(0, targetFileContents)
            });

            archiveHelper.WriteFlatFileContents(targetFilePath, result, targetIsCompressed, dictionaryId);

            TracePrint("{0}: Wrote changelog for {1} into {2}", modFolderName, Path.GetFileName(filePath), 
                                   pathRelativeToBase);
        }
    }

    private void MergeArchivesInMod(string modFolderPath) {
        
        if (!Directory.Exists(modFolderPath))
            throw new Exception($"The input mod folder '{modFolderPath}' could not be found.");

        var filesInModFolder = Directory.GetFiles(modFolderPath, "*", SearchOption.AllDirectories)
                                        .Where(file => SarcPackager.SupportedExtensions.Any(
                                                   ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

        Parallel.ForEach(filesInModFolder, filePath => {
            var pathRelativeToBase = Path.GetRelativePath(modFolderPath, Path.GetDirectoryName(filePath)!);
            TracePrint("{0}: Merging {1}", modFolderPath, filePath);

            try {
                MergeArchive(modFolderPath, filePath, pathRelativeToBase);
            } catch (InvalidDataException) {
                Trace.TraceWarning("Invalid archive: {0} - can't merge so overwriting by priority", filePath);
                var targetArchivePath = Path.Combine(outputPath, pathRelativeToBase, Path.GetFileName(filePath));

                if (File.Exists(targetArchivePath))
                    File.Delete(targetArchivePath);

                CopyHelper.CopyFile(filePath, targetArchivePath);
            } catch (Exception) {
                Trace.TraceError("Failed to merge {0}", filePath);
                throw;
            }
        });
        
    }

    private void CleanPackagesInTarget() {
        if (!Directory.Exists(outputPath))
            return;
        
        Trace.TraceWarning("Cleaning existing archives in output {0}", outputPath);

        var filesInOutputFolder = Directory.GetFiles(outputPath, "*", SearchOption.AllDirectories);
        foreach (var filePath in filesInOutputFolder.Where(file => SarcPackager.SupportedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))) {
            File.Delete(filePath);
        }
    }

    private void MergeArchive(string modFolderPath, string archivePath, string pathRelativeToBase) {
        // If the output doesn't even exist just copy it over and we're done
        var targetArchivePath = Path.Combine(outputPath, pathRelativeToBase, Path.GetFileName(archivePath));

        if (!File.Exists(targetArchivePath)) {
            if (!Directory.Exists(Path.GetDirectoryName(targetArchivePath)))
                Directory.CreateDirectory(Path.GetDirectoryName(targetArchivePath)!);
            
            var didCopy = CopyOriginal(archivePath, pathRelativeToBase, targetArchivePath);
            
            // Copy the mod's package to the output if we otherwise failed to copy the file from the dump
            if (!didCopy) {
                CopyHelper.CopyFile(archivePath, targetArchivePath);
                return;
            }
        }
        
        // Otherwise try to reconcile and merge
        var isCompressed = archivePath.EndsWith(".zs");

        Span<byte> sourceFileContents = archiveHelper.GetFileContents(archivePath, isCompressed, out _);
        Span<byte> targetFileContents = archiveHelper.GetFileContents(targetArchivePath, isCompressed, out var dictionaryId);

        var sourceSarc = Sarc.FromBinary(sourceFileContents.ToArray());
        var targetSarc = Sarc.FromBinary(targetFileContents.ToArray());

        foreach (var entry in sourceSarc) {
            if (!targetSarc.ContainsKey(entry.Key)) {
                // If the archive doesn't have the file, add it
                targetSarc.Add(entry.Key, entry.Value);
            } else {
                // Otherwise, reconcile with the handler
                var fileExtension = Path.GetExtension(entry.Key);

                if (String.IsNullOrWhiteSpace(fileExtension)) {
                    Trace.TraceWarning("{0}: {1} does not have a file extension! Including as-is in {2}", modFolderPath, 
                                       entry.Key, archivePath);
                    
                    targetSarc[entry.Key] = entry.Value;
                    continue;
                }
                
                // Drop the . from the extension
                fileExtension = fileExtension.Substring(1);
                var handler = handlerManager.GetHandlerInstance(fileExtension);

                if (handler == null) {
                    TracePrint("{0}: Wrote {1} in {2} as-is", modFolderPath,
                                       entry.Key, targetArchivePath);
                    targetSarc[entry.Key] = entry.Value;
                    continue;
                }
                
                var result = handler.Merge(entry.Key, new List<MergeFile>() {
                    new MergeFile(1, entry.Value),
                    new MergeFile(0, targetSarc[entry.Key])
                });

                targetSarc[entry.Key] = result.ToArray();

                TracePrint("{0}: Merged changelog {1} to {2}", modFolderPath, entry.Key, targetArchivePath);
            }
        }

        archiveHelper.WriteFileContents(targetArchivePath, targetSarc, isCompressed, dictionaryId);
    }
    

    private bool CopyOriginal(string archivePath, string pathRelativeToBase, string outputFile) {
        var sourcePath = config.GamePath;
        var originalFile = Path.Combine(sourcePath, pathRelativeToBase, Path.GetFileName(archivePath));

        if (File.Exists(originalFile)) {
            CopyHelper.CopyFile(originalFile, outputFile);
            return true;
        }

        return false;
    }

    private void TracePrint(string message, params object?[]? elements) {
        if (Verbose)
            Trace.TraceInformation(message, elements);
    }

    
}