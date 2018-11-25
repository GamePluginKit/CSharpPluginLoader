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
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;

using UnityEngine;
using GamePluginKit.API;
using Debug = UnityEngine.Debug;

[assembly: StartupBehaviour(typeof(ScriptPluginLoader))]

class ScriptPluginLoader : MonoBehaviour
{
    static readonly string GpkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePluginKit"
    );

    void Start()
    {
        var files    = new List<string>();
        var modsPath = Path.Combine(Application.dataPath, "Mods");
        var plgnPath = Path.Combine(GpkDir, "Plugins");

        files.AddRange(Directory.GetFiles(modsPath, "*.cs", SearchOption.AllDirectories));
        files.AddRange(Directory.GetFiles(plgnPath, "*.cs", SearchOption.AllDirectories));

        var asm  = Compile(files);
        var attr = asm.GetCustomAttributes(typeof(StartupBehaviourAttribute), false);

        foreach (var plugin in attr.Cast<StartupBehaviourAttribute>())
        {
            var go = new GameObject(plugin.BehaviourType.Name, plugin.BehaviourType);
            go.transform.SetParent(transform);
        }
    }

    Assembly Compile(IList<string> files)
    {
        var csc = GetCompiler();
        csc.StandardInput.WriteLine("SetDataPath");
        csc.StandardInput.WriteLine(Application.dataPath);

        foreach (string path in files)
        {
            csc.StandardInput.WriteLine("AddSourceFile");
            csc.StandardInput.WriteLine(path);
        }

        csc.StandardInput.WriteLine("Compile");

        try
        {
            var success = bool.Parse(csc.StandardOutput.ReadLine());

            if (success)
            {
                string outputPath = csc.StandardOutput.ReadLine();
                return Assembly.LoadFrom(outputPath);
            }
            else
            {
                int messageCount = int.Parse(csc.StandardOutput.ReadLine());

                for (int i = 0; i < messageCount; i++)
                {
                    Debug.LogError(csc.StandardOutput.ReadLine());
                }

                return null;
            }
        }
        finally
        {
            csc.StandardInput.WriteLine("Finish");
        }
    }

    Process GetCompiler()
    {
        var cscPath = Path.Combine(Path.Combine(GpkDir, "Tools"), "ScriptPluginLoader.Compiler.exe");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false
            }
        };

        switch (Application.platform)
        {
        case RuntimePlatform.WindowsPlayer:
            process.StartInfo.FileName = cscPath;
            break;
        case RuntimePlatform.LinuxPlayer:
        case RuntimePlatform.OSXPlayer:
            process.StartInfo.FileName = "mono";
            process.StartInfo.Arguments = '"' + cscPath.Replace("\"", "\\\"") + '"';
            break;
        }

        process.Start();
        process.WaitForInputIdle();

        return process;
    }
}
