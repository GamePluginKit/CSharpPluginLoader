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
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

class Program
{
    static void Main(string[] args)
    {
        using (var input  = new StreamReader(Console.OpenStandardInput()))
        using (var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true })
        {
            var dataPath = string.Empty;
            var trees    = new List<SyntaxTree>();
            var defines  = new List<string>();

            while (true)
            {
                switch (input.ReadLine())
                {
                case "AddDefine":
                    defines.Add(input.ReadLine());
                    break;
                case "SetDataPath":
                    dataPath = input.ReadLine();
                    break;
                case "AddSourceFile":
                    AddSourceFile(input.ReadLine());
                    break;
                case "Compile":
                    Compile();
                    break;
                case "Finish":
                    return;
                }
            }

            void Compile()
            {
                var references = new List<MetadataReference>();
                var managedDir = Path.Combine(dataPath, "Managed");
                var modsDir    = Path.Combine(dataPath, "Mods");
                var gpkDir     = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamePluginKit");
                var pluginsDir = Path.Combine(gpkDir, "Plugins");
                var coreDir    = Path.Combine(gpkDir, "Core");

                // Create references for all assemblies
                AddReferences(managedDir);
                AddReferences(pluginsDir);
                AddReferences(modsDir);
                AddReferences(coreDir);

                var outputPath = Path.GetTempFileName();
                var options = new CSharpCompilationOptions(
                    outputKind: OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    allowUnsafe: true
                );

                var compilation = CSharpCompilation.Create("ScriptAssembly", trees, references, options);
                var result      = compilation.Emit(outputPath);

                output.WriteLine(result.Success);

                if (result.Success)
                    output.WriteLine(outputPath);
                else
                {
                    // todo: maybe send a JSON-ified version of the diagnostics
                    // I dunno, this needs work, it's far too basic right now.

                    output.WriteLine(result.Diagnostics.Length);

                    foreach (var diagnostic in result.Diagnostics)
                        output.WriteLine(diagnostic.GetMessage());
                }

                void AddReferences(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly)
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*.dll", searchOption))
                    {
                        references.Add(MetadataReference.CreateFromFile(file));
                    }
                }
            }

            void AddSourceFile(string filePath)
            {
                var options = CSharpParseOptions.Default
                    .WithLanguageVersion(LanguageVersion.Latest)
                    .WithPreprocessorSymbols(defines);

                var text = File.ReadAllText(filePath);
                var tree = CSharpSyntaxTree.ParseText(text, options);

                trees.Add(tree);
            }
        }
    }
}
