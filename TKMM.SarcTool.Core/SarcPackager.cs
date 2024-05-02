using System.Diagnostics;
using SarcLibrary;
using TKMM.SarcTool.Core.Model;

namespace TKMM.SarcTool.Core;

public class SarcPackager {
    private readonly ConfigJson config;
    private readonly ZsCompression compression;
    private readonly ChecksumLookup checksumLookup;
    private readonly HandlerManager handlerManager;
    private string[] versions;
    private readonly string outputPath, modPath;
    
    internal static readonly string[] SupportedExtensions = new[] {
        ".bfarc", ".bkres", ".blarc", ".genvb", ".pack", ".ta",
        ".bfarc.zs", ".bkres.zs", ".blarc.zs", ".genvb.zs", ".pack.zs", ".ta.zs"
    };

    public SarcPackager(string outputPath, string modPath, string? configPath = null, string? checksumPath = null, string[]? checkVersions = null) {
        this.handlerManager = new HandlerManager();
        this.outputPath = outputPath;
        this.modPath = modPath;
        configPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Totk", "config.json");
        
        checksumPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "Totk", "checksums.bin");

        checkVersions ??= new[] {"100", "110", "111", "112", "120", "121"};

        if (!File.Exists(configPath))
            throw new Exception($"{configPath} not found");

        if (!File.Exists(checksumPath))
            throw new Exception($"{checksumPath} not found");

        this.config = ConfigJson.Load(configPath);

        if (String.IsNullOrWhiteSpace(this.config.GamePath))
            throw new Exception("Game path is not defined in config.json");

        var compressionPath = Path.Combine(this.config.GamePath, "Pack", "ZsDic.pack.zs");
        if (!File.Exists(compressionPath)) {
            throw new Exception("Compression package not found: {this.config.GamePath}");
        }

        compression = new ZsCompression(compressionPath);
        checksumLookup = new ChecksumLookup(checksumPath);

        this.modPath = modPath;
        this.versions = checkVersions;


    }

    public void Package() {
        InternalMakePackage();
    }
    
   

    private void InternalMakePackage() {
        string[] filesInFolder = Directory.GetFiles(modPath, "*", SearchOption.AllDirectories);
        
        foreach (var filePath in filesInFolder.Where(file => SupportedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))) {
            var pathRelativeToBase = Path.GetRelativePath(modPath, Path.GetDirectoryName(filePath)!);
            var destinationPath = Path.Combine(outputPath, pathRelativeToBase);
            if (!Directory.Exists(destinationPath))
                Directory.CreateDirectory(destinationPath);

            var outputFilePath = Path.Combine(destinationPath, Path.GetFileName(filePath));
            
            try {
                var result = HandleArchive(filePath, pathRelativeToBase);

                if (result.Length == 0) {
                    Trace.TraceInformation("Omitting {0}: Same as vanilla");
                    continue;
                }

                // Copy to destination
                if (File.Exists(outputFilePath)) {
                    Trace.TraceWarning("Overwriting {0}", outputFilePath);
                    File.Delete(outputFilePath);
                }

                File.WriteAllBytes(outputFilePath, result.ToArray());

                Trace.TraceInformation("Packaged {0}", outputFilePath);
            } catch (Exception exc) {
                Trace.TraceError("Failed to package {0} - Error: {1} - skipping", filePath, exc.Message);
                
                if (File.Exists(outputFilePath)) {
                    Trace.TraceWarning("Overwriting {0}", outputFilePath);
                    File.Delete(outputFilePath);
                }

                File.Copy(filePath, outputFilePath, true);
            }
        }

        Trace.TraceInformation("Packaging flat files to {0}", outputPath);
        PackageFilesInMod();

        Trace.TraceInformation("Creating GDL changelog");
        PackageGameDataList();


    }

    private Span<byte> HandleArchive(string archivePath, string pathRelativeToBase) {
        
        var isCompressed = archivePath.EndsWith(".zs");
        var isPackFile = archivePath.Contains(".pack.");

        var fileContents = GetFileContents(archivePath, isCompressed, isPackFile);

        var archiveHash = Checksum.ComputeXxHash(fileContents);
        
        // Identical archives don't need to be processed or copied
        if (IsArchiveIdentical(archivePath, pathRelativeToBase, archiveHash))
            return Span<byte>.Empty;

        var sarc = Sarc.FromBinary(fileContents.ToArray());
        var originalSarc = GetOriginalArchive(Path.GetFileName(archivePath), pathRelativeToBase, isCompressed, isPackFile);
        var isVanillaFile = IsVanillaFile(GetArchiveRelativeFilename(Path.GetFileName(archivePath), pathRelativeToBase));
        var toRemove = new List<string>();
        var atLeastOneReplacement = false;

        foreach (var entry in sarc) {
            var fileHash = Checksum.ComputeXxHash(entry.Value);

            var filenameHashSource = (Path.Combine(pathRelativeToBase, Path.GetFileName(archivePath)) + "/" + entry.Key)
                                     .Replace(Path.DirectorySeparatorChar, '/')
                                     .Replace("romfs/", "");
            
            // Remove identical items from the SARC
            if (IsFileIdentical(filenameHashSource, fileHash) || IsFileIdentical(entry.Key, fileHash)) {
                toRemove.Add(entry.Key);
            } else if (originalSarc != null) {
                // Perform merge against the original file if we have an archive in the dump
                
                if (!originalSarc.ContainsKey(entry.Key))
                    continue;
                
                // Otherwise, reconcile with the handler
                var fileExtension = Path.GetExtension(entry.Key).Substring(1);
                var handler = handlerManager.GetHandlerInstance(fileExtension);

                if (handler == null) {
                    Trace.TraceWarning("No handler for {0} {1} - overwriting contents", archivePath, entry.Key);
                    sarc[entry.Key] = entry.Value;
                    continue;
                }
                
                var result = handler.Package(entry.Key, new List<MergeFile>() {
                    new MergeFile(1, entry.Value),
                    new MergeFile(0, originalSarc[entry.Key])
                });

                sarc[entry.Key] = result.ToArray();
                atLeastOneReplacement = true;
            }
        }
        
        // Nothing to remove? We can skip it
        if (toRemove.Count == 0 && !atLeastOneReplacement && isVanillaFile)
            return Span<byte>.Empty;
        
        // Removals
        foreach (var entry in toRemove)
            sarc.Remove(entry);

        Span<byte> outputContents;
        
        if (isCompressed) {
            using var memoryStream = new MemoryStream();
            sarc.Write(memoryStream);
            outputContents = compression.Compress(memoryStream.ToArray(),
                                                  isPackFile ? CompressionType.Pack : CompressionType.Common);
        } else {
            using var memoryStream = new MemoryStream();
            sarc.Write(memoryStream);
            outputContents = memoryStream.ToArray();
        }

        return outputContents;
    }

    private void PackageGameDataList() {
       var gdlFilePath = Path.Combine(modPath, "romfs", "GameData");

        if (!Directory.Exists(gdlFilePath))
            return;
        
        var files = Directory.GetFiles(gdlFilePath);

        var gdlMerger = new GameDataListMerger();

        foreach (var gdlFile in files) {

            try {
                if (!Path.GetFileName(gdlFile).StartsWith("GameDataList.Product"))
                    continue;

                var isCompressed = gdlFile.EndsWith(".zs");

                var vanillaFilePath = Path.Combine(config!.GamePath!, "GameData", Path.GetFileName(gdlFile));

                if (!File.Exists(vanillaFilePath)) {
                    throw new Exception("Failed to find vanilla GameDataList file");
                }

                var isVanillaCompressed = vanillaFilePath.EndsWith(".zs");

                var vanillaFile = GetFlatFileContents(vanillaFilePath, isVanillaCompressed);
                var modFile = GetFlatFileContents(gdlFile, isCompressed);

                var changelog = gdlMerger.Package(vanillaFile, modFile);

                if (changelog.Length == 0) {
                    Trace.TraceInformation("No changes in GDL");
                    continue;
                }

                var targetFilePath = Path.Combine(outputPath, "romfs", "GameData", "GameDataList.gdlchangelog");

                if (!Directory.Exists(Path.GetDirectoryName(targetFilePath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
                
                File.WriteAllBytes(targetFilePath, changelog.ToArray());

                Trace.TraceInformation("Created GDL changelog");
                
                // Only need one change log
                break;
            } catch {
                Trace.TraceError("Failed to create GDL changelog");
                throw;
            }

        }

    }

    private void PackageFilesInMod() {
        var filesInModFolder =
            Directory.GetFiles(modPath, "*", SearchOption.AllDirectories);

        var supportedFlatExtensions = handlerManager.GetSupportedExtensions().ToHashSet();
        supportedFlatExtensions =
            supportedFlatExtensions.Concat(supportedFlatExtensions.Select(l => $"{l}.zs")).ToHashSet();

        var folderExclusions = new[] {"RSDB"};
        var extensionExclusions = new[] {".rstbl.byml", ".rstbl.byml.zs"};
        var prefixExclusions = new[] {"GameDataList.Product"};

        foreach (var filePath in filesInModFolder) {
            if (!supportedFlatExtensions.Any(l => filePath.EndsWith(l)))
                continue;

            if (folderExclusions.Any(l => filePath.Contains(Path.DirectorySeparatorChar + l + Path.DirectorySeparatorChar)))
                continue;

            if (extensionExclusions.Any(l => filePath.EndsWith(l)))
                continue;

            if (prefixExclusions.Any(l => Path.GetFileName(filePath).StartsWith(l)))
                continue;

            var baseRomfs = Path.Combine(modPath, "romfs");
            var pathRelativeToBase = Path.GetRelativePath(baseRomfs, Path.GetDirectoryName(filePath)!);

            PackageFile(filePath, pathRelativeToBase);

            Trace.TraceInformation("Created {0} in {1}", filePath, pathRelativeToBase);
        }
    }

    private void PackageFile(string filePath, string pathRelativeToBase) {
        var targetFilePath = Path.Combine(outputPath, "romfs", pathRelativeToBase, Path.GetFileName(filePath));
        var vanillaFilePath = Path.Combine(config!.GamePath!, pathRelativeToBase, Path.GetFileName(filePath));

        // If the vanilla file doesn't exist just copy it over and we're done
        if (!File.Exists(vanillaFilePath)) {
            File.Copy(filePath, targetFilePath, true);
            return;
        }
        
        // Create the target
        if (!Directory.Exists(Path.GetDirectoryName(targetFilePath)))
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);

        // Otherwise try to reconcile and merge
        var isCompressed = filePath.EndsWith(".zs");

        if (isCompressed && !targetFilePath.EndsWith(".zs"))
            targetFilePath += ".zs";

        var vanillaFileContents = GetFlatFileContents(vanillaFilePath, isCompressed);
        var targetFileContents = GetFlatFileContents(filePath, isCompressed);

        var fileExtension = Path.GetExtension(filePath).Substring(1).ToLower();
        var handler = handlerManager.GetHandlerInstance(fileExtension);

        if (handler == null) {
            Trace.TraceWarning("No handler for {0} {1} - overwriting contents", Path.GetFileName(filePath), 
                               pathRelativeToBase);
            
            File.Copy(filePath, targetFilePath, true);
        } else {
            var relativeFilename = Path.Combine(pathRelativeToBase, Path.GetFileName(filePath));

            if (Path.DirectorySeparatorChar != '/')
                relativeFilename = relativeFilename.Replace(Path.DirectorySeparatorChar, '/');

            var result = handler.Package(relativeFilename, new List<MergeFile>() {
                new MergeFile(0, vanillaFileContents),
                new MergeFile(1, targetFileContents)
            });

            WriteFlatFileContents(targetFilePath, result, isCompressed);
        }
    }

    internal void WriteFileContents(string archivePath, Sarc sarc, bool isCompressed, bool isPackFile) {
        if (compression == null)
            throw new Exception("Compression not loaded");

        using var memoryStream = new MemoryStream();
        sarc.Write(memoryStream);

        if (isCompressed) {
            var type = CompressionType.Common;

            // Change compression type
            if (isPackFile)
                type = CompressionType.Pack;
            else if (archivePath.Contains("bcett", StringComparison.OrdinalIgnoreCase))
                type = CompressionType.Bcett;
            
            File.WriteAllBytes(archivePath,
                               compression.Compress(memoryStream.ToArray(), type)
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
            var type = CompressionType.Common;

            // Change compression type
            if (isPackFile)
                type = CompressionType.Pack;
            else if (archivePath.Contains("bcett", StringComparison.OrdinalIgnoreCase))
                type = CompressionType.Bcett;
            
            var compressedContents = File.ReadAllBytes(archivePath).AsSpan();
            sourceFileContents = compression.Decompress(compressedContents, type);
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
            var type = CompressionType.Common;

            // Change compression type
            if (filePath.Contains("bcett", StringComparison.OrdinalIgnoreCase))
                type = CompressionType.Bcett;
            
            var compressedContents = File.ReadAllBytes(filePath).AsSpan();
            sourceFileContents = compression.Decompress(compressedContents, type);
        } else {
            sourceFileContents = File.ReadAllBytes(filePath).AsSpan();
        }

        return new Memory<byte>(sourceFileContents.ToArray());
    }

    private void WriteFlatFileContents(string filePath, ReadOnlyMemory<byte> contents, bool isCompressed) {
        if (compression == null)
            throw new Exception("Compression not loaded");

        if (isCompressed) {
            var type = CompressionType.Common;

            // Change compression type
            if (filePath.Contains("bcett", StringComparison.OrdinalIgnoreCase))
                type = CompressionType.Bcett;
            
            File.WriteAllBytes(filePath,
                               compression.Compress(contents.ToArray(), type).ToArray());
        } else {
            File.WriteAllBytes(filePath, contents.ToArray());
        }
    }

    private bool IsFileIdentical(string filename, ulong fileHash) {
        var filenameHash = Checksum.ComputeXxHash(filename);
        
        if (checksumLookup!.GetChecksum(filenameHash) == fileHash)
            return true;

        foreach (var version in versions) {
            var versionHash = Checksum.ComputeXxHash(filename + "#" + version);
            if (checksumLookup!.GetChecksum(versionHash) == fileHash)
                return true;
        }

        return false;
    }

    private bool IsVanillaFile(string filename) {
        var filenameHash = Checksum.ComputeXxHash(filename);
        return checksumLookup!.GetChecksum(filenameHash) != null;
    }

    private bool IsArchiveIdentical(string archivePath, string pathRelativeToBase, ulong archiveHash) {
        var archiveRelativeFilename = GetArchiveRelativeFilename(archivePath, pathRelativeToBase);

        // Hash of the filename and contents
        var filenameHash = Checksum.ComputeXxHash(archiveRelativeFilename);

        if (checksumLookup!.GetChecksum(filenameHash) == archiveHash)
            return true;

        foreach (var version in versions) {
            var versionHash = Checksum.ComputeXxHash(archiveRelativeFilename + "#" + version);
            if (checksumLookup!.GetChecksum(versionHash) == archiveHash)
                return true;
        }

        return false;

    }

    private static string GetArchiveRelativeFilename(string archivePath, string pathRelativeToBase) {
        // Relative filename
        var pathSeparator = Path.DirectorySeparatorChar;
        var archiveRelativeFilename = Path.Combine(pathRelativeToBase, Path.GetFileName(archivePath));
        
        // Replace the path separator with the one used by the Switch
        if (pathSeparator != '/')
            archiveRelativeFilename = archiveRelativeFilename.Replace(pathSeparator, '/');

        // Get rid of any romfs portion of the path
        archiveRelativeFilename = archiveRelativeFilename.Replace("/romfs/", "")
                                                         .Replace("romfs/", "");

        // Get rid of any .zs on the end if the file was originally compressed
        if (archiveRelativeFilename.EndsWith(".zs"))
            archiveRelativeFilename = archiveRelativeFilename.Substring(0, archiveRelativeFilename.Length - 3);
        return archiveRelativeFilename;
    }

    private Sarc? GetOriginalArchive(string archiveFile, string pathRelativeToBase, bool isCompressed, bool isPackFile) {

        // Get rid of /romfs/ in the path
        var directoryChar = Path.DirectorySeparatorChar;
        pathRelativeToBase = pathRelativeToBase.Replace($"romfs{directoryChar}", "")
                                               .Replace($"{directoryChar}romfs{directoryChar}", "");
        
        var archivePath = Path.Combine(config!.GamePath!, pathRelativeToBase, archiveFile);

        if (!File.Exists(archivePath))
            return null;
        
        Span<byte> fileContents;
        if (isCompressed) {
            // Need to decompress the file first
            var type = CompressionType.Common;

            // Change compression type
            if (isPackFile)
                type = CompressionType.Pack;
            else if (archivePath.Contains("bcett", StringComparison.OrdinalIgnoreCase))
                type = CompressionType.Bcett;
            
            var compressedContents = File.ReadAllBytes(archivePath).AsSpan();
            fileContents = compression!.Decompress(compressedContents, type);
        } else {
            fileContents = File.ReadAllBytes(archivePath).AsSpan();
        }

        return Sarc.FromBinary(fileContents.ToArray());
    }

}