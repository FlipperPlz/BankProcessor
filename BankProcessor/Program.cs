using System.Text;
using BisUtils.DZConfig;
using BisUtils.Param.Models;
using BisUtils.Param.Options;
using BisUtils.RVBank.Extensions;
using BisUtils.RVBank.Model;
using BisUtils.RVBank.Model.Entry;
using BisUtils.RVBank.Model.Stubs;
using BisUtils.RVBank.Options;
using Mono.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

public sealed class Program
{
    public static Program Instance = null!;
    private static int currentLogLevel = 6;
    private readonly string inputPath;
    private readonly RVBankOptions bankOptions;
    private readonly ParamOptions paramOptions;

    private readonly IEnumerable<RVBank> banks;
    private readonly Dictionary<IRVBankDataEntry, IDzConfig> modConfigs = new();


    private void ProcessBanks()
    {
        {
            var configs = LocateConfigEntries(true).ToList();
            if (!configs.Any())
            {
                Log.Logger.Fatal(
                    "No config files were found in any of the supplied banks, perhaps you're missing a bank?");
            }

            ProcessConfigs(configs);
        }


    }

    private void ProcessConfigs(IEnumerable<IRVBankDataEntry> configs)
    {
        Log.Logger.Information("Stage 1.1 Starting... (parse configs)");
        foreach (var bankEntry in configs)
        {
            var bankName = bankEntry.BankFile.FileName;
            Log.Logger.Information("[{bankName}]: Parsing config file in '{configPath}'...", bankName, bankEntry.AbsolutePath);
            try
            {
                var file = ParamFile.ReadParamFile("config", bankEntry.EntryData, paramOptions);
                Log.Logger.Information("[{bankName}]: Config file was parsed successfully", bankName);

                var config = new DzConfig(file!);
                if (!(config.CfgPatches?.Any() ?? false))
                {
                    Log.Logger.Information("No patches were found in the config. Assuming dummy...");
                    continue;
                }
                Log.Logger.Information("Found patch(es) {patches} in config.", string.Join(", ", config.CfgPatches!.Select(it => it.PatchName + $"(depends on [{string.Join(", ", it.Dependencies ?? ArraySegment<string?>.Empty)}])")));
            }
            catch (Exception e)
            {
                Dump(bankEntry.EntryData, false);
                Console.WriteLine(e);
                throw;
            }

        }
    }

    public IEnumerable<IRVBankDataEntry> LocateConfigEntries(bool deleteFakeConfigs = true)
    {
        Log.Logger.Information("Stage 1: Starting config scan on {bankCount} bank(s)", banks.Count());

        var dirs = new Dictionary<IRVBankDirectory, IRVBankDataEntry>();

        foreach (var bank in banks)
        {


            Log.Logger.Information("Scanning bank {bankPath} ({bankPrefix}) for config files.", bank.FileName, bank.BankPrefix);
            foreach (var binnedConfig in bank.GetFileEntries("config.bin", true))
            {
                Log.Logger.Information("[{bankName}] Located binarized addon config entry at '{cfgPath}' under prefix {bankPrefix}", bank.FileName, binnedConfig.AbsolutePath, bank.BankPrefix);
                dirs[binnedConfig.ParentDirectory] = binnedConfig;
            }

            foreach (var config in bank.GetFileEntries("config.cpp", true))
            {
                Log.Logger.Information("[{bankName}] Located addon config entry at '{cfgPath}' under prefix {bankPrefix}", bank.FileName, config.AbsolutePath, bank.BankPrefix);
                dirs[config.ParentDirectory] = config;
            }
        }
        Log.Logger.Information("Config scanning has been completed for banks. {configCount} config(s) were found in {modCount}", dirs.Count, banks.Count());
        return new List<IRVBankDataEntry>(dirs.Values);
    }

    public static void Main(string[] arguments)
    {
        var inputPath = string.Empty;
        var outputPath = string.Empty;
        var timeout = TimeSpan.FromMinutes(1);

        var commandOptions = new OptionSet()
            .Add("v", "Increase verbosity", e => --currentLogLevel)
            .Add("f=|folder=", "Input Folder", s => inputPath = s)
            .Add("o=|output=", "Output Folder", s => outputPath = s)
            .Add("dc=|dcTimeout=", "Decompression Timeout (not yet implemented)", (int? it) =>
            {
                if (it is { } nnIt)
                {
                    timeout = TimeSpan.FromMinutes(nnIt);
                }
            });
        commandOptions.Parse(arguments);
        var logSink = Path.GetTempFileName();
#pragma warning disable CA1305
        Log.Logger = new LoggerConfiguration().WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", restrictedToMinimumLevel: (LogEventLevel)currentLogLevel)
#pragma warning restore CA1305
            .WriteTo
            .File(new JsonFormatter(), logSink, restrictedToMinimumLevel: (LogEventLevel)currentLogLevel).CreateLogger();

        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Log.Fatal("Could not find a file nor a directory at '{inputPath}'", inputPath);
        }

        if (Directory.Exists(outputPath))
        {
            Log.Warning("{output} exists and is not empty.", outputPath);
            Directory.Delete(outputPath, true);
        }

        Directory.CreateDirectory(outputPath);

        var options = new RVBankOptions()
        {
            AllowObfuscated = true,
            FlatRead = false,
            RemoveBenignProperties = true,
            AllowMultipleVersion = true,
            IgnoreValidation = true,
            DecompressionTimeout = timeout!,
            Charset = Encoding.UTF8,
            RequireValidSignature = false,
            RegisterEmptyEntries = false,
            CompressionErrorsAreWarnings = false,
            RequireEmptyVersionMeta = false,
            RequireVersionMimeOnVersion = false,
            RequireFirstEntryIsVersion = false,
            RequireVersionNotNamed = false,
            AllowVersionMimeOnData = true,
            IgnoreInvalidStreamSize = true,
            AlwaysSeparateOnDummy = true,
            AllowUnnamedDataEntries = true,
            AllowDuplicateFileNames = true,
            RespectEntryOffsets = false,
            WriteValidOffsets = false,
            AsciiLengthTimeout = 510,
            AllowEncrypted = true
        };

        Instance = Directory.Exists(inputPath) ? new Program(DiscoverBanks(inputPath, outputPath, options), inputPath) : new Program(new[] { RVBank.ReadPbo(inputPath, options, syncTo: null) }, inputPath) ;
    }

    private static IEnumerable<RVBank> DiscoverBanks(string inputPath, string outputPath, RVBankOptions options) =>
        Directory.EnumerateFiles(inputPath, "*.pbo", SearchOption.AllDirectories).Select(it => RVBank.ReadPbo(it, options, null));

    private Program(IEnumerable<RVBank> banks, string inputPath)
    {
        this.banks = banks;
        this.inputPath = inputPath;
        paramOptions = new ParamOptions()
        {
            DuplicateClassnameIsError = false,
            MissingDeleteTargetIsError = false,
            MissingParentIsError = false,
            IgnoreValidation = true
        };

        ProcessBanks();
    }

    private void Dump(Stream data, bool crash = true)
    {
        Log.Logger.Information("Fatal error parsing parsing config.");
        using var binaryReader = new BinaryReader(data);
        var bytes = binaryReader.ReadBytes((int)data.Length);

        Log.Logger.Information(Encoding.UTF8.GetString(bytes));
        if(crash) Environment.Exit(999);
    }

}
