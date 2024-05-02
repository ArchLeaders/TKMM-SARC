using System.Diagnostics;
using SarcLibrary;
using TKMM.SarcTool.Core.Model;

namespace TKMM.SarcTool.Core;

public class SarcAssembler {
    
    private readonly ConfigJson config;
    private ZsCompression compression;
    private Dictionary<string, string> archiveMappings = new Dictionary<string, string>();

    private readonly string modPath;
    private readonly string configPath;

    public SarcAssembler(string modPath, string? configPath = null) {
        configPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Totk", "config.json");

        if (!File.Exists(configPath))
            throw new Exception($"{configPath} not found");

        this.config = ConfigJson.Load(configPath);

        if (String.IsNullOrWhiteSpace(this.config.GamePath))
            throw new Exception("Game path is not defined in config.json");

        var compressionPath = Path.Combine(this.config.GamePath, "Pack", "ZsDic.pack.zs");
        if (!File.Exists(compressionPath)) {
            throw new Exception("Compression package not found: {this.config.GamePath}");
        }

        compression = new ZsCompression(compressionPath);

        this.modPath = modPath;
        this.configPath = configPath;
    }

    public void Assemble() {

        LoadArchiveCache();
        InternalAssemble();
        
    }

    private void InternalAssemble() {

        var supportedExtensions = new[] {"byml", "byaml"};

        var flatFiles = Directory.GetFiles(modPath, "*", SearchOption.AllDirectories)
                                 .Where(l => supportedExtensions.Any(
                                            ext => l.EndsWith(ext) || l.EndsWith(ext + ".zs")))
                                 .ToList();

        foreach (var file in flatFiles) {
            var relativeFilePath = GetRelativePath(file, modPath);
            
            if (!archiveMappings.TryGetValue(relativeFilePath, out var archiveRelativePath)) {
                continue;
            }

            if (!MergeIntoArchive(archiveRelativePath, file, relativeFilePath)) {
                Trace.TraceWarning("Skipping {0} - could not assemble", file);
                continue;
            }
            
            // Success means we delete the flat file
            File.Delete(file);

        }
        
    }

    private bool MergeIntoArchive(string archiveRelativePath, string filePath, string fileRelativePath) {

        var archivePath = GetAbsolutePath(archiveRelativePath, modPath);

        // First test the existing archive
        if (!File.Exists(archivePath))
            archivePath += ".zs";
        if (!File.Exists(archivePath) && !CopyVanillaArchive(archiveRelativePath, archivePath))
            return false;

        var isCompressed = archivePath.EndsWith(".zs");
        var archiveContents = GetFileContents(archivePath, isCompressed, true);
        var sarc = Sarc.FromBinary(archiveContents.ToArray());

        var isFileCompressed = filePath.EndsWith(".zs");
        var fileContents = GetFileContents(filePath, isFileCompressed, false);

        // Skip if the SARC doesn't contain the file already
        if (!sarc.ContainsKey(fileRelativePath)) {
            sarc.Add(fileRelativePath, fileContents.ToArray());
        } else {
            sarc[fileRelativePath] = fileContents.ToArray();
        }

        WriteFileContents(archivePath, sarc, isCompressed, true);
        return true;

    }

    private bool CopyVanillaArchive(string archiveRelativePath, string destination) {
        var vanillaPath = GetAbsolutePath(archiveRelativePath, config!.GamePath!);

        if (!File.Exists(vanillaPath))
            vanillaPath += ".zs";
        if (!File.Exists(vanillaPath))
            return false;

        File.Copy(vanillaPath, destination, true);
        return true;
    }

    private void LoadArchiveCache() {
        var archiveCachePath = Path.Combine(configPath, "archivemappings.bin");

        if (!File.Exists(archiveCachePath)) {
            CreateArchiveCache(archiveCachePath);
        } else {
            LoadArchiveCacheFromDisk(archiveCachePath);
        }
    }

    private void CreateArchiveCache(string archiveCachePath) {

        var supportedExtensions = new[] {
            ".pack.zs", ".pack"
        };

        Trace.TraceInformation($"Preparing to create archive cache");

        var dumpArchives = Directory.GetFiles(config!.GamePath!, "*", SearchOption.AllDirectories)
                                    .Where(l => supportedExtensions.Any(ext => l.EndsWith(ext)))
                                    .ToList();

        Trace.TraceInformation("Creating archive cache (this may take a bit)");
        
        foreach (var file in dumpArchives) {
            var isCompressed = file.EndsWith(".zs");
            var relativeArchivePath = GetRelativePath(file, config!.GamePath!);

            try {
                var archiveContents = GetFileContents(file, isCompressed, true);
                var sarc = Sarc.FromBinary(archiveContents.ToArray());

                foreach (var key in sarc.Keys)
                    archiveMappings.TryAdd(key, relativeArchivePath);
                
            } catch (Exception exc) {
                Trace.TraceError("Couldn't load {0} - Error: {1} - Skipping", file, exc.Message);
            }
        }

        SerializeCacheToDisk(archiveCachePath);

    }

    private void LoadArchiveCacheFromDisk(string inputFile) {
        using var inputStream = new FileStream(inputFile, FileMode.Open);
        using var reader = new BinaryReader(inputStream);

        var magic = reader.ReadChars(4);

        if (new string(magic) != "STMC")
            throw new InvalidDataException("Cache has invalid header");

        var version = reader.ReadInt16();

        if (version != 1)
            throw new InvalidDataException($"Cache does not support version {version}");

        var itemCount = reader.ReadInt32();

        archiveMappings = new Dictionary<string, string>();

        for (int i = 0; i < itemCount; i++) {
            var key = reader.ReadString();
            var value = reader.ReadString();

            archiveMappings.TryAdd(key, value);
        }

        reader.Close();
        inputStream.Close();
    }

    private void SerializeCacheToDisk(string outputFile) {

        using var outputStream = new FileStream(outputFile, FileMode.Create);
        using var writer = new BinaryWriter(outputStream);

        // Header
        writer.Write("STMC".ToCharArray());       // Magic
        writer.Write((short)1);                   // Version

        writer.Write(archiveMappings.Count);      // Item count
        
        foreach (var item in archiveMappings) {
            writer.Write(item.Key);
            writer.Write(item.Value);
        }

        writer.Flush();
        outputStream.Flush();
        outputStream.Close();

    }

    private string GetRelativePath(string archivePath, string basePath) {
        var pathRelativeToBase = Path.GetRelativePath(basePath, archivePath);

        if (Path.DirectorySeparatorChar != '/')
            pathRelativeToBase = pathRelativeToBase.Replace(Path.DirectorySeparatorChar, '/');

        pathRelativeToBase = pathRelativeToBase.Replace($"romfs/", "")
                                               .Replace($"/romfs/", "");

        if (pathRelativeToBase.EndsWith(".zs"))
            pathRelativeToBase = pathRelativeToBase.Substring(0, pathRelativeToBase.Length - 3);

        return pathRelativeToBase;

    }

    private string GetAbsolutePath(string relativePath, string basePath) {
        if (Path.DirectorySeparatorChar != '/')
            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        if (!basePath.Contains("romfs"))
            return Path.Combine(basePath, "romfs", relativePath);
        else
            return Path.Combine(basePath, relativePath);
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
            
            File.WriteAllBytes(archivePath, compression.Compress(memoryStream.ToArray(), type).ToArray());
        } else {
            File.WriteAllBytes(archivePath, memoryStream.ToArray());
        }
    }
}