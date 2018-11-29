// Copyright 2018 Benjamin Moir
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;

using UnityEngine;
using GamePluginKit;
using static GamePluginKit.GpkEnvironment;
using Debug = UnityEngine.Debug;

// This plugin allows you to provide .cs source files,
// instead of the usual compiled .dll assemblies.
//
// The benefits of this lie mainly in conditional compilation,
// inter-plugin compatibility, and ease of use.
//
// The plugin makes use of an external compiler application
// that communicates with the game via stdin and stdout.

[assembly: StartupBehaviour(typeof(CSharpPluginLoader))]

class CSharpPluginLoader : MonoBehaviour
{
    const string CompilerExe = "CSharpPluginLoader.Compiler.exe";

    void Awake()
    {
        // Recursively search for .cs source files in the game-
        // specific mod folder, and the global plugins folder.
        var files = new List<string>();
        files.AddRange(Directory.GetFiles(ModsPath,    "*.cs", SearchOption.AllDirectories));
        files.AddRange(Directory.GetFiles(PluginsPath, "*.cs", SearchOption.AllDirectories));

        var plugin = Compile(files);
        var attrs  = plugin.GetCustomAttributes(typeof(StartupBehaviourAttribute), false);

        foreach (var attr in attrs.Cast<StartupBehaviourAttribute>())
        {
            var go = new GameObject(attr.BehaviourType.Name, attr.BehaviourType);
            go.transform.SetParent(transform);
        }
    }

    Assembly Compile(IList<string> files)
    {
        // Let's get a Process object for starting the compiler
        // We then start it, and ensure that it is ready for use
        var csc = GetCompilerProcess();
        csc.Start();
        csc.WaitForInputIdle();

        // Now we need to send our commands to the compiler,
        // and receive all of the output it gives us.
        using (var br = new BinaryReader(csc.StandardOutput.BaseStream))
        using (var bw = new BinaryWriter(csc.StandardInput .BaseStream))
        {
            // For simplicity purposes, the compiler can only
            // do one compilation per invocation, so there is
            // no action to start a "new" compilation. We just
            // immediately start setting things up.

            // Preprocessor symbols should be set up first.
            foreach (var symbol in new[]
                {
                    "UNITY_STANDALONE"
                })
            {
                bw.Write((int)CompilerAction.AddPreprocessorSymbol);
                bw.Write(symbol);
            }

            bw.Write((int)CompilerAction.AddPreprocessorSymbol);
            switch (Application.platform)
            {
            case RuntimePlatform.WindowsPlayer:
                bw.Write("UNITY_STANDALONE_WIN");
                break;
            case RuntimePlatform.LinuxPlayer:
                bw.Write("UNITY_STANDALONE_LINUX");
                break;
            case RuntimePlatform.OSXPlayer:
                bw.Write("UNITY_STANDALONE_OSX");
                break;
            default:
                bw.Write("UNITY_STANDALONE_UNKNOWN");
                break;
            }

            if (Debug.isDebugBuild)
            {
                bw.Write((int)CompilerAction.AddPreprocessorSymbol);
                bw.Write("DEVELOPMENT_BUILD");
            }

            // Create UNITY_X, UNITY_X_Y and UNITY_X_Y_Z symbol
            var split = Application.unityVersion.Split('.', 'b', 'f', 'p');

            for (int n = 1; n <= Mathf.Min(split.Length, 3); n++)
            {
                bw.Write((int)CompilerAction.AddPreprocessorSymbol);
                bw.Write($"UNITY_" + string.Join("_", split.Take(n).ToArray()));
            }

            // Create UNITY_X_Y_OR_NEWER symbols
            var current = new Version(
                int.Parse(split.Length > 0 ? split[0] : "0"),
                int.Parse(split.Length > 1 ? split[1] : "0"),
                int.Parse(split.Length > 2 ? split[2] : "0"),
                int.Parse(split.Length > 3 ? split[3] : "0")
            );

            AddOrNewerSymbols(5, 3, 6);
            AddOrNewerSymbols(2017);
            AddOrNewerSymbols(2018);
            AddOrNewerSymbols(2019);

            void AddOrNewerSymbols(int major, int minorStart = 1, int minorEnd = 9)
            {
                for (int i = minorStart; i <= minorEnd; i++)
                {
                    var version = new Version(major, i);

                    if (version > current) continue;

                    bw.Write((int)CompilerAction.AddPreprocessorSymbol);
                    bw.Write($"UNITY_{major}_{i}_OR_NEWER");
                }
            }

            // Older Unity games that use the "micro" mscorlib
            // can cause problems. The compiler has special handling
            // for these games through a special type forwarding assembly.
            if (GetPublicKeyToken(typeof(object).Assembly) == "7cec85d7bea7798e")
            {
                bw.Write((int)CompilerAction.EnableMicro);
                bw.Write((int)CompilerAction.AddPreprocessorSymbol);
                bw.Write("MICRO_MSCORLIB_BUILD");
            }

            string GetPublicKeyToken(Assembly assembly)
            {
                var bytes = assembly.GetName().GetPublicKeyToken();

                if (bytes == null || bytes.Length == 0) return null;

                return string.Join(string.Empty, bytes.Select(b => $"{b:x2}").ToArray());
            }

            // Now we add all of the source files and references
            foreach (string path in files)
            {
                bw.Write((int)CompilerAction.AddSourceFile);
                bw.Write(path);
            }

            var references = new List<string>();
            references.AddRange(Directory.GetFiles(CorePath,    "*.dll", SearchOption.AllDirectories));
            references.AddRange(Directory.GetFiles(PluginsPath, "*.dll", SearchOption.AllDirectories));
            references.AddRange(Directory.GetFiles(ModsPath,    "*.dll", SearchOption.AllDirectories));
            references.AddRange(Directory.GetFiles(ManagedPath, "*.dll", SearchOption.AllDirectories));

            foreach (string path in references)
            {
                bw.Write((int)CompilerAction.AddReference);
                bw.Write(path);
            }

            // Now that the compilation is all set up, it's time to compile it
            bw.Write((int)CompilerAction.Compile);
            bw.Flush();

            try
            {
                // Depending on the success value, CompilerAction.Compile can return either:
                // true:  An length integer and a byte array (the bytes of the compiled assembly)
                // false: A length integer and a CompilerDiagnostic array (information on what went wrong)
                bool success = br.ReadBoolean();
                int length   = br.ReadInt32();

                if (success)
                {
                    var bytes = br.ReadBytes(length);
                    return Assembly.Load(bytes);
                }

                // todo: finish the CompilerDiagnostic feature.
                // Currently the compiler simply returns all messages.
                Debug.LogError("Compilation of script plugins failed.");

                for (var i = 0; i < length; i++)
                    Debug.LogError(br.ReadString());

                return null;
            }
            finally
            {
                // Lastly, we need to send the compiler the Finish action,
                // which simply closes it.
                bw.Write((int)CompilerAction.Finish);
                bw.Flush();
            }
        }
    }

    Process GetCompilerProcess()
    {
        var cscPath = Path.Combine(Path.Combine(ToolsPath, "CSCompiler"), CompilerExe);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
                UseShellExecute        = false
            }
        };

        // On Windows we can just call the executable directly,
        // however on other desktop platforms we will need to
        // invoke it through Mono. Unfortunately this means that
        // we will have to manually escape some arguments.
        switch (Application.platform)
        {
        case RuntimePlatform.WindowsPlayer:
            process.StartInfo.FileName = cscPath;
            break;
        case RuntimePlatform.LinuxPlayer:
        case RuntimePlatform.OSXPlayer:
            process.StartInfo.FileName  = "mono";
            process.StartInfo.Arguments = NonWindowsEscape(cscPath);
            break;
        }

        return process;

        string NonWindowsEscape(params string[] arguments)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    sb.Append(' ');

                sb.Append('"');

                foreach (char c in arguments[i])
                {
                    switch (c)
                    {
                    case '\u0060': case '\u007e': case '\u0021':
                    case '\u0023': case '\u0024': case '\u0026':
                    case '\u002a': case '\u0028': case '\u0029':
                    case '\u0009': case '\u007b': case '\u005b':
                    case '\u007c': case '\u005c': case '\u003b':
                    case '\u0027': case '\u0022': case '\u000a':
                    case '\u003c': case '\u003e': case '\u003f':
                    case '\u0020': case '\u003d':
                        sb.Append('\\');
                        break;
                    }

                    sb.Append(c);
                }

                sb.Append('"');
            }

            return sb.ToString();
        }
    }
}
