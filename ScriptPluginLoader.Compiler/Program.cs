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
using System.Reflection;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

class Program
{
    static void Main(string[] args)
    {
        // Register all of the compiler methods
        var actions = new Dictionary<CompilerAction, Action<CompilerContext>>();

        foreach (var method in typeof(Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
        {
            var attr = method.GetCustomAttribute<CompilerMethodAttribute>();

            if (attr == null) continue;

            actions.Add(attr.Action, (Action<CompilerContext>)method.CreateDelegate(typeof(Action<CompilerContext>)));
        }

        using (var br = new BinaryReader(Console.OpenStandardInput()))
        using (var bw = new BinaryWriter(Console.OpenStandardOutput()))
        {
            // Create the compilation and begin listening for actions
            var ctx = new CompilerContext(br, bw)
            {
                Compilation = CSharpCompilation.Create("ScriptPluginAssembly")
                    .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(true)
                    .WithOptimizationLevel(OptimizationLevel.Release)),

                ParseOptions = CSharpParseOptions.Default
                    .WithKind(SourceCodeKind.Regular)
                    .WithLanguageVersion(LanguageVersion.Latest),
            };

            while (true)
            {
                var action = (CompilerAction)br.ReadInt32();

                if (actions.TryGetValue(action, out var method))
                {
                    method(ctx);
                }

                if (action == CompilerAction.Finish)
                    return;
            }
        }
    }

    [CompilerMethod(CompilerAction.AddPreprocessorSymbol)]
    static void AddDefine(CompilerContext ctx)
    {
        var define       = ctx.Reader.ReadString();
        var symbols      = new List<string>(ctx.ParseOptions.PreprocessorSymbolNames) { define };
        ctx.ParseOptions = ctx.ParseOptions.WithPreprocessorSymbols(symbols);
    }

    [CompilerMethod(CompilerAction.AddSourceFile)]
    static void AddSourceFile(CompilerContext ctx)
    {
        string path = ctx.Reader.ReadString();
        string text = File.ReadAllText(path);

        var syntaxTree  = CSharpSyntaxTree.ParseText(text, ctx.ParseOptions);
        ctx.Compilation = ctx.Compilation.AddSyntaxTrees(syntaxTree);
    }

    [CompilerMethod(CompilerAction.AddReference)]
    static void AddReference(CompilerContext ctx)
    {
        string path     = ctx.Reader.ReadString();
        var reference   = MetadataReference.CreateFromFile(path);
        ctx.Compilation = ctx.Compilation.AddReferences(reference);
    }

    [CompilerMethod(CompilerAction.EnableMicro)]
    static void EnableMicro(CompilerContext ctx)
    {
        var rootPath    = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        string path     = Path.Combine(rootPath, Path.Combine("MicroForwarder", "mscorlib.dll"));
        var reference   = MetadataReference.CreateFromFile(path);
        ctx.Compilation = ctx.Compilation.AddReferences(reference);
    }

    [CompilerMethod(CompilerAction.Compile)]
    static void Compile(CompilerContext ctx)
    {
        var ms     = new MemoryStream();
        var result = ctx.Compilation.Emit(ms, options: new EmitOptions()
            .WithIncludePrivateMembers(true)
            .WithTolerateErrors(true));

        ctx.Writer.Write(result.Success);

        if (result.Success)
        {
            ctx.Writer.Write((int)ms.Length);
            ctx.Writer.Write(ms.ToArray());
        }
        else
        {
            ctx.Writer.Write(result.Diagnostics.Length);

            foreach (var diag in result.Diagnostics)
                ctx.Writer.Write(diag.GetMessage());
        }

        ctx.Writer.Flush();
    }
}
