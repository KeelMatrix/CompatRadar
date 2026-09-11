namespace KeelMatrix.CompatRadar;

internal static class RuntimePreviewAdapter
{
    private static readonly HashSet<string> MsBuildCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "build",
        "msbuild",
        "pack",
        "publish",
        "run",
        "test"
    };

    public static bool TryPrepare(
        ParsedCommand validationCommand,
        string runtimeVersion,
        out ParsedCommand? preparedCommand,
        out string? error)
    {
        preparedCommand = null;
        error = null;

        if (!IsDotnetCommand(validationCommand) || validationCommand.Arguments.Count == 0)
        {
            error = "runtime-preview requires a dotnet build, test, run, pack, publish, msbuild, or direct application validation command.";
            return false;
        }

        var verb = validationCommand.Arguments[0];
        if (MsBuildCommands.Contains(verb))
        {
            if (validationCommand.Arguments.Any(argument => argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
            {
                error = "runtime-preview requires a validation command that builds the application so its runtime configuration can be generated.";
                return false;
            }

            preparedCommand = AddMsBuildRuntimeOverride(validationCommand, runtimeVersion);
            preparedCommand = AddNoSharedCompilationOverride(preparedCommand);
            return true;
        }

        if (IsDirectApplication(validationCommand))
        {
            preparedCommand = AddHostRuntimeOverride(validationCommand, runtimeVersion);
            return true;
        }

        error = "runtime-preview requires a dotnet build, test, run, pack, publish, msbuild, or direct application validation command.";
        return false;
    }

    public static bool IsExactRuntimeInstalled(string listRuntimesOutput, string runtimeVersion)
    {
        return listRuntimesOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(parts => parts.Length >= 2
                && parts[0].Equals("Microsoft.NETCore.App", StringComparison.Ordinal)
                && parts[1].Equals(runtimeVersion, StringComparison.Ordinal));
    }

    private static ParsedCommand AddMsBuildRuntimeOverride(ParsedCommand command, string runtimeVersion)
    {
        var arguments = RemoveMsBuildProperties(command.Arguments, "RuntimeFrameworkVersion", "RollForward");
        var separatorIndex = arguments.FindIndex(argument => argument == "--");
        var insertionIndex = separatorIndex >= 0 ? separatorIndex : arguments.Count;
        arguments.Insert(insertionIndex++, $"-p:RuntimeFrameworkVersion={runtimeVersion}");
        arguments.Insert(insertionIndex, "-p:RollForward=Disable");
        return new ParsedCommand(command.FileName, arguments);
    }

    private static ParsedCommand AddHostRuntimeOverride(ParsedCommand command, string runtimeVersion)
    {
        var arguments = RemoveHostOptions(command.Arguments, "--fx-version", "--roll-forward");
        arguments.Insert(0, "--fx-version");
        arguments.Insert(1, runtimeVersion);
        arguments.Insert(2, "--roll-forward");
        arguments.Insert(3, "Disable");
        return new ParsedCommand(command.FileName, arguments);
    }

    private static ParsedCommand AddNoSharedCompilationOverride(ParsedCommand command)
    {
        if (command.Arguments.Any(argument => argument.Equals("-p:UseSharedCompilation=false", StringComparison.OrdinalIgnoreCase)))
        {
            return command;
        }

        var arguments = command.Arguments.ToList();
        var separatorIndex = arguments.FindIndex(argument => argument == "--");
        if (separatorIndex >= 0)
        {
            arguments.Insert(separatorIndex, "-p:UseSharedCompilation=false");
        }
        else
        {
            arguments.Add("-p:UseSharedCompilation=false");
        }

        return new ParsedCommand(command.FileName, arguments);
    }

    private static List<string> RemoveMsBuildProperties(IReadOnlyList<string> arguments, params string[] names)
    {
        var result = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            var property = argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase)
                ? argument[3..].Split('=', 2)[0]
                : argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase)
                    ? argument[3..].Split('=', 2)[0]
                    : null;
            if (property is not null && names.Contains(property, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(argument);
        }

        return result;
    }

    private static List<string> RemoveHostOptions(IReadOnlyList<string> arguments, params string[] names)
    {
        var result = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (names.Contains(arguments[index], StringComparer.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            result.Add(arguments[index]);
        }

        return result;
    }

    private static bool IsDirectApplication(ParsedCommand command)
    {
        var firstArgument = command.Arguments[0];
        return firstArgument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || firstArgument.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotnetCommand(ParsedCommand command)
    {
        var fileName = Path.GetFileNameWithoutExtension(command.FileName);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }
}
