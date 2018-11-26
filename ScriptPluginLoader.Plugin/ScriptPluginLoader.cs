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
using Debug = UnityEngine.Debug;

// This plugin allows you to provide .cs source files,
// instead of the usual compiled .dll assemblies.
//
// The benefits of this lie mainly in conditional compilation,
// inter-plugin compatibility, and ease of use.
//
// The plugin makes use of an external compiler application
// that communicates with the game via stdin and stdout.

[assembly: StartupBehaviour(typeof(ScriptPluginLoader))]

class ScriptPluginLoader : MonoBehaviour
{
    const string CompilerExe = "ScriptPluginLoader.Compiler.exe";

    // GPK's root is defined as <LocalAppData>/GamePluginKit/,
    // or wherever Mono remaps that path to on your system.
    static readonly string GpkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePluginKit"
    );

    static readonly string CoreDir = Path.Combine(GpkDir, "Core");

    static readonly string PluginsDir = Path.Combine(GpkDir, "Plugins");

    static readonly string ToolsDir = Path.Combine(GpkDir, "Tools");

    static readonly string ModsDir = Path.Combine(Application.dataPath, "Mods");

    static readonly string ManagedDir = Path.Combine(Application.dataPath, "Managed");

    void Awake()
    {
        // Recursively search for .cs source files in the game-
        // specific mod folder, and the global plugins folder.
        var files = new List<string>();
        files.AddRange(Directory.GetFiles(ModsDir,    "*.cs", SearchOption.AllDirectories));
        files.AddRange(Directory.GetFiles(PluginsDir, "*.cs", SearchOption.AllDirectories));

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

            // Older Unity games that use the "micro" mscorlib
            // can cause problems. The compiler has special handling
            // for these games through a special type forwarding assembly.
            if (GetPublicKey(typeof(object).Assembly) == "7cec85d7bea7798e")
                bw.Write((int)CompilerAction.EnableMicro);

            string GetPublicKey(Assembly assembly)
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
            references.AddRange(Directory.GetFiles(CoreDir,    "*.dll", SearchOption.AllDirectories));
            references.AddRange(Directory.GetFiles(PluginsDir, "*.dll", SearchOption.AllDirectories));
            references.AddRange(Directory.GetFiles(ModsDir,    "*.dll", SearchOption.AllDirectories));
            references.AddRange(Directory.GetFiles(ManagedDir, "*.dll", SearchOption.AllDirectories));

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
        var cscPath = Path.Combine(ToolsDir, CompilerExe);
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
            var escapeChars = new[]
            {
                '\u0060', '\u007e', '\u0021', '\u0023',
                '\u0024', '\u0026', '\u002a', '\u0028',
                '\u0029', '\u0009', '\u007b', '\u005b',
                '\u007c', '\u005c', '\u003b', '\u0027',
                '\u0022', '\u000a', '\u003c', '\u003e',
                '\u003f', '\u0020', '\u003d'
            };

            var sb = new StringBuilder();

            foreach (string arg in arguments)
            {
                sb.Append('"');

                foreach (char c in arg)
                {
                    if (escapeChars.Contains(c))
                        sb.Append('\\');
                    sb.Append(c);
                }

                sb.Append('"');
                sb.Append(' ');
            }

            return sb.ToString();
        }
    }
}
