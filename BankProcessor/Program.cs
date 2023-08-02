using System.Text;
using BisUtils.DZConfig;
using BisUtils.Param.Models;
using BisUtils.Param.Options;
using BisUtils.RVBank.Model;
using BisUtils.RVBank.Model.Entry;
using BisUtils.RVBank.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Mono.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace BankProcessor;

using BisUtils.Extensions.RVBank.DzConfigExtensions;
using BisUtils.RVBank.Extensions;

public sealed class Program
{
    // ReSharper disable once NotAccessedField.Local
    private static Program instance = null!;
    private static int currentLogLevel = 6;
    private static string inputPath = string.Empty;
    private static string outputPath = string.Empty;
    private static TimeSpan compressionTimeout = TimeSpan.FromMinutes(1);
    private static RVBankOptions bankOptions = null!;
    private static ParamOptions paramOptions = null!;
    private static ILogger msLogger = NullLogger.Instance;

    private readonly IEnumerable<RVBank> banks;
    private readonly Dictionary<IRVBankDataEntry, IDzConfig> modConfigs = new();


    private void ProcessBanks()
    {
        {
            var configs = LocateConfigEntries().ToList();
            if (!configs.Any())
            {
                Log.Logger.Fatal(
                    "No config files were found in any of the supplied banks, perhaps you're missing a bank?");
            }

            ProcessConfigs(configs);
        }


    }

    private static void ProcessConfigs(IEnumerable<IRVBankDataEntry> configs)
    {
        Log.Logger.Information("Stage 1.1 Starting... (parse configs)");
        foreach (var bankEntry in configs)
        {
            var bankName = bankEntry.BankFile.FileName;
            Log.Logger.Information("[{bankName}]: Parsing config file in '{configPath}'...", bankName, bankEntry.AbsolutePath);
            try
            {
                var file = ParamFile.ReadParamFile("config", bankEntry.EntryData, paramOptions, msLogger);
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

        var configFiles = new List<IRVBankDataEntry>();

        foreach (var bank in banks)
        {
            Log.Logger.Information("Scanning bank {bankPath} ({bankPrefix}) for config files.", bank.FileName, bank.BankPrefix);
            var found = bank.LocateConfigEntries(SearchOption.AllDirectories).ToList();
            Log.Logger.Information("Located {cfgCount} config(s)", found.Count);

            configFiles.AddRange(found);
        }
        Log.Logger.Information("Config scanning has been completed for banks. {configCount} config(s) were found in {modCount}", configFiles.Count, banks.Count());
        return configFiles;
    }

    public static void Main(string[] arguments)
    {

        InitializeArguments(arguments);
        InitializeLogger();

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

        paramOptions = new ParamOptions
        {
            DuplicateClassnameIsError = false,
            MissingDeleteTargetIsError = false,
            MissingParentIsError = false,
            IgnoreValidation = true
        };

        bankOptions = new RVBankOptions()
        {
            AllowObfuscated = true,
            FlatRead = false,
            RemoveBenignProperties = true,
            AllowMultipleVersion = true,
            IgnoreValidation = true,
            DecompressionTimeout = compressionTimeout,
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
            IgnoreDuplicateFiles = true,
            RespectEntryOffsets = false,
            WriteValidOffsets = false,
            AsciiLengthTimeout = 1023,
            AllowEncrypted = true
        };

        instance = Directory.Exists(inputPath) ? new Program(DiscoverBanks()) : new Program(new[] { new RVBank(inputPath, bankOptions, syncTo: null, msLogger) }) ;
    }

    private static void InitializeArguments(string[] arguments)
    {
        var commandOptions = new OptionSet()
            .Add("v", "Increase verbosity",_ => --currentLogLevel)
            .Add("f=|folder=", "Input Folder", s => inputPath = s)
            .Add("o=|output=", "Output Folder", s => outputPath = s)
            .Add("dc=|dcTimeout=", "Decompression Timeout (not yet implemented)", (int? it) =>
            {
                if (it is { } nnIt)
                {
                    compressionTimeout = TimeSpan.FromMinutes(nnIt);
                }
            });
        commandOptions.Parse(arguments);
    }

    private static void InitializeLogger()
    {
        var logSink = Path.GetTempFileName();
#pragma warning disable CA1305
        Log.Logger = new LoggerConfiguration().WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", restrictedToMinimumLevel: (LogEventLevel)currentLogLevel)
#pragma warning restore CA1305
            .WriteTo
            .File(new JsonFormatter(), logSink, restrictedToMinimumLevel: (LogEventLevel)currentLogLevel).CreateLogger();


        msLogger = new SerilogLoggerFactory().CreateLogger("Program");
    }

    private static IEnumerable<RVBank> DiscoverBanks() =>
        Directory.EnumerateFiles(inputPath, "*.pbo", SearchOption.AllDirectories).Select(it => new RVBank(it, bankOptions, null, msLogger));

    private Program(IEnumerable<RVBank> banks)
    {
        this.banks = banks;


        ProcessBanks();
    }

    private static void Dump(Stream data, bool crash = true)
    {
        Log.Logger.Information("Fatal error parsing parsing config.");
        using var binaryReader = new BinaryReader(data);
        var bytes = binaryReader.ReadBytes((int)data.Length);

        Log.Logger.Information(Encoding.UTF8.GetString(bytes));
        if(crash)
        {
            Environment.Exit(999);
        }
    }

}
