﻿using Microsoft.Win32;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Base.Utilities;
using MinecraftLaunch.Extensions;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace MinecraftLaunch.Utilities;

public static partial class JavaUtil {
    public static async Task<JavaEntry> GetJavaInfoAsync(string javaPath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath)) {
            return null;
        }

        using var process = Process.Start(new ProcessStartInfo(javaPath) {
            Arguments = "-version",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        string text = process.StandardError.ReadToEnd()?.ToLower();
        if (string.IsNullOrEmpty(text))
            throw new ArgumentNullException();

        bool is64bit = text.Contains("64-bit");
        string javaVersion = JavaVersionRegex()
            .Match(text).Groups[1].Value
            .Split('_')?.FirstOrDefault();

        string javaType = text.Contains("java(tm)")
            ? "Java"
            : text.Contains("zulu")
                ? "ZuluJDK"
                : "OpenJDK";

        await process.WaitForExitAsync(cancellationToken);
        var _ = int.TryParse(javaVersion, out var version);

        return new JavaEntry {
            Is64bit = is64bit,
            JavaPath = javaPath,
            JavaType = javaType,
            JavaVersion = version is 0 && !string.IsNullOrWhiteSpace(javaVersion) 
                ? new(javaVersion) 
                : new(version, 0),
        };
    }

    public static async IAsyncEnumerable<JavaEntry> EnumerableJavaAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) {
        if (EnvironmentUtil.IsWindow) {
            foreach (var java in GetJavasForWindows().AsParallel()) {
                yield return await GetJavaInfoAsync(java, cancellationToken);
            }

            yield break;
        }

        //Use by:https://github.com/Corona-Studio/ProjBobcat/blob/master/ProjBobcat/ProjBobcat/Class/Helper/DeepJavaSearcher.cs
        using var process = Process.Start(new ProcessStartInfo("whereis") {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = {
                "/b",
                "java"
            },
        });

        if (process == null)
            yield break;

        do {
            cancellationToken.ThrowIfCancellationRequested();

            var line = process.StandardOutput.ReadLine();
            if (string.IsNullOrEmpty(line) || !File.Exists(line))
                continue;

            yield return await GetJavaInfoAsync(line, cancellationToken);
        } while (!process.HasExited);

        await process.WaitForExitAsync(cancellationToken);
        var lastLine = await process.StandardOutput.ReadLineAsync(cancellationToken);

        if (!string.IsNullOrEmpty(lastLine) || File.Exists(lastLine))
            yield return await GetJavaInfoAsync(lastLine, cancellationToken)!;
    }

    #region Privates

    [GeneratedRegex(@"version\s+""([\d]+)""")]
    private static partial Regex JavaVersionRegex();

    [SupportedOSPlatform("Windows")]
    private static IEnumerable<string> GetJavasForWindows() {
        //Use by:https://github.com/Xcube-Studio/Natsurainko.FluentCore/blob/main/Natsurainko.FluentCore/Environment/JavaUtils.cs
        List<string> result = [];

        #region Cmd: Find Java by running "where javaw" command in cmd.exe

        using var process = new Process() {
            StartInfo = new ProcessStartInfo() {
                FileName = "cmd",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true,
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var output = new List<string>();

        process.OutputDataReceived += (sender, e) => output.Add(e.Data);
        process.ErrorDataReceived += (sender, e) => output.Add(e.Data);

        process.StandardInput.WriteLine("where javaw");
        process.StandardInput.WriteLine("exit");
        process.WaitForExit();

        IEnumerable<string> javaPaths = output.Where(
            x => !string.IsNullOrEmpty(x) && x.EndsWith("javaw.exe") && File.Exists(x)
        )!; // null checked in the where clause
        result.AddRange(javaPaths);

        #endregion

        #region Registry: Find Java by searching the registry

        var javaHomePaths = new List<string>();

        // Local function: recursively search for the keyName in the registry
        static List<string> ForRegistryKey(RegistryKey registryKey, string keyName) {
            var result = new List<string>();

            foreach (string valueName in registryKey.GetValueNames()) {
                if (valueName == keyName) // Check that the valueName exists
                    result.Add((string)registryKey.GetValue(valueName)!);
            }

            foreach (string registrySubKey in registryKey.GetSubKeyNames()) {
                using var subKey = registryKey.OpenSubKey(registrySubKey);
                if (subKey is not null) // Check that the registrySubKey exists
                    result.AddRange(ForRegistryKey(subKey, keyName));
            }

            return result;
        };

        using var reg = Registry.LocalMachine.OpenSubKey("SOFTWARE");

        if (reg is not null && reg.GetSubKeyNames().Contains("JavaSoft")) {
            using var registryKey = reg.OpenSubKey("JavaSoft");
            if (registryKey is not null)
                javaHomePaths.AddRange(ForRegistryKey(registryKey, "JavaHome"));
        }

        if (reg is not null && reg.GetSubKeyNames().Contains("WOW6432Node")) {
            using var registryKey = reg.OpenSubKey("WOW6432Node");
            if (registryKey is not null && registryKey.GetSubKeyNames().Contains("JavaSoft")) {
                using var registrySubKey = reg.OpenSubKey("JavaSoft");
                if (registrySubKey is not null)
                    ForRegistryKey(registrySubKey, "JavaHome").ForEach(x => javaHomePaths.Add(x));
            }
        }

        foreach (var item in javaHomePaths)
            if (Directory.Exists(item))
                result.AddRange(new DirectoryInfo(item).FindAll("javaw.exe").Select(x => x.FullName));

        #endregion

        #region Special Folders

        List<string> folders = [];

        // %APPDATA%\.minecraft\cache\java
        string appDataPath = Environment.GetEnvironmentVariable("APPDATA");

        if (!string.IsNullOrEmpty(appDataPath))
            folders.Add(Path.Combine(appDataPath, ".minecraft\\cache\\java"));

        // %APPDATA%\.minecraft\runtime\
        if (!string.IsNullOrEmpty(appDataPath))
            folders.Add(Path.Combine(appDataPath, ".minecraft\\runtime\\"));

        // %JAVA_HOME%
        string javaHomePath = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (javaHomePath is not null)
            folders.Add(javaHomePath);

        // Program Files\Java
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"));

        // Program Files\Zulu
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zulu"));

        // Check Java for each folder
        foreach (var folder in folders)
            if (Directory.Exists(folder))
                result.AddRange(new DirectoryInfo(folder).FindAll("javaw.exe").Select(x => x.FullName));

        #endregion

        return result.Distinct();
    }

    #endregion
}