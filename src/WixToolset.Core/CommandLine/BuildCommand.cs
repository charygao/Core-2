// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.CommandLine
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Xml.Linq;
    using WixToolset.Data;
    using WixToolset.Extensibility;
    using WixToolset.Extensibility.Data;
    using WixToolset.Extensibility.Services;

    internal class BuildCommand : ICommandLineCommand
    {
        private readonly CommandLine commandLine;

        public BuildCommand(IServiceProvider serviceProvider)
        {
            this.ServiceProvider = serviceProvider;
            this.Messaging = serviceProvider.GetService<IMessaging>();
            this.ExtensionManager = serviceProvider.GetService<IExtensionManager>();
            this.commandLine = new CommandLine(this.Messaging);
        }

        public bool ShowLogo => this.commandLine.ShowLogo;

        public bool StopParsing => this.commandLine.ShowHelp;

        private IServiceProvider ServiceProvider { get; }

        private IMessaging Messaging { get; }

        private IExtensionManager ExtensionManager { get; }

        private string IntermediateFolder { get; set; }

        private OutputType OutputType { get; set; }

        private List<string> IncludeSearchPaths { get; set; }

        private Platform Platform { get; set; }

        private string OutputFile { get; set; }

        private string ContentsFile { get; set; }

        private string OutputsFile { get; set; }

        private string BuiltOutputsFile { get; set; }

        public int Execute()
        {
            if (this.commandLine.ShowHelp)
            {
                Console.WriteLine("TODO: Show build command help");
                return -1;
            }

            this.IntermediateFolder = this.commandLine.CalculateIntermedateFolder();

            this.OutputType = this.commandLine.CalculateOutputType();

            this.IncludeSearchPaths = this.commandLine.IncludeSearchPaths;

            this.Platform = this.commandLine.Platform;

            this.OutputFile = this.commandLine.OutputFile;

            this.ContentsFile = this.commandLine.ContentsFile;

            this.OutputsFile = this.commandLine.OutputsFile;

            this.BuiltOutputsFile = this.commandLine.BuiltOutputsFile;

            var preprocessorVariables = this.commandLine.GatherPreprocessorVariables();

            var sourceFiles = this.commandLine.GatherSourceFiles(this.IntermediateFolder);

            var filterCultures = this.commandLine.CalculateFilterCultures();

            var creator = this.ServiceProvider.GetService<ITupleDefinitionCreator>();

            this.EvaluateSourceFiles(sourceFiles, creator, out var codeFiles, out var wixipl);

            if (this.Messaging.EncounteredError)
            {
                return this.Messaging.LastErrorNumber;
            }

            var wixobjs = this.CompilePhase(preprocessorVariables, codeFiles);

            var wxls = this.LoadLocalizationFiles(this.commandLine.LocalizationFilePaths, preprocessorVariables);

            if (this.Messaging.EncounteredError)
            {
                return this.Messaging.LastErrorNumber;
            }

            if (this.OutputType == OutputType.Library)
            {
                var wixlib = this.LibraryPhase(wixobjs, wxls, this.commandLine.BindFiles, this.commandLine.BindPaths);

                if (!this.Messaging.EncounteredError)
                {
                    wixlib.Save(this.commandLine.OutputFile);
                }
            }
            else
            {
                if (wixipl == null)
                {
                    wixipl = this.LinkPhase(wixobjs, this.commandLine.LibraryFilePaths, creator);
                }

                if (!this.Messaging.EncounteredError)
                {
                    if (this.OutputType == OutputType.IntermediatePostLink)
                    {
                        wixipl.Save(this.commandLine.OutputFile);
                    }
                    else
                    {
                        this.BindPhase(wixipl, wxls, filterCultures, this.commandLine.CabCachePath, this.commandLine.BindPaths);
                    }
                }
            }

            return this.Messaging.LastErrorNumber;
        }

        public bool TryParseArgument(ICommandLineParser parser, string argument)
        {
            return this.commandLine.TryParseArgument(argument, parser);
        }

        private void EvaluateSourceFiles(IEnumerable<SourceFile> sourceFiles, ITupleDefinitionCreator creator, out List<SourceFile> codeFiles, out Intermediate wixipl)
        {
            codeFiles = new List<SourceFile>();

            wixipl = null;

            foreach (var sourceFile in sourceFiles)
            {
                var extension = Path.GetExtension(sourceFile.SourcePath);

                if (wixipl != null || ".wxs".Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    codeFiles.Add(sourceFile);
                }
                else
                {
                    try
                    {
                        wixipl = Intermediate.Load(sourceFile.SourcePath, creator);
                    }
                    catch (WixException)
                    {
                        // We'll assume anything that isn't a valid intermediate is source code to compile.
                        codeFiles.Add(sourceFile);
                    }
                }
            }

            if (wixipl == null && codeFiles.Count == 0)
            {
                this.Messaging.Write(ErrorMessages.NoSourceFiles());
            }
            else if (wixipl != null && codeFiles.Count != 0)
            {
                this.Messaging.Write(ErrorMessages.WixiplSourceFileIsExclusive());
            }
        }

        private IEnumerable<Intermediate> CompilePhase(IDictionary<string, string> preprocessorVariables, IEnumerable<SourceFile> sourceFiles)
        {
            var intermediates = new List<Intermediate>();

            foreach (var sourceFile in sourceFiles)
            {
                var document = this.Preprocess(preprocessorVariables, sourceFile.SourcePath);

                if (this.Messaging.EncounteredError)
                {
                    continue;
                }

                var context = this.ServiceProvider.GetService<ICompileContext>();
                context.Extensions = this.ExtensionManager.Create<ICompilerExtension>();
                context.OutputPath = sourceFile.OutputPath;
                context.Platform = this.Platform;
                context.Source = document;

                Intermediate intermediate = null;
                try
                {
                    var compiler = this.ServiceProvider.GetService<ICompiler>();
                    intermediate = compiler.Compile(context);
                }
                catch (WixException e)
                {
                    this.Messaging.Write(e.Error);
                }

                if (this.Messaging.EncounteredError)
                {
                    continue;
                }

                intermediates.Add(intermediate);
            }

            return intermediates;
        }

        private Intermediate LibraryPhase(IEnumerable<Intermediate> intermediates, IEnumerable<Localization> localizations, bool bindFiles, IEnumerable<BindPath> bindPaths)
        {
            var context = this.ServiceProvider.GetService<ILibraryContext>();
            context.BindFiles = bindFiles;
            context.BindPaths = bindPaths;
            context.Extensions = this.ExtensionManager.Create<ILibrarianExtension>();
            context.Localizations = localizations;
            context.Intermediates = intermediates;

            Intermediate library = null;
            try
            {
                var librarian = this.ServiceProvider.GetService<ILibrarian>();
                library = librarian.Combine(context);
            }
            catch (WixException e)
            {
                this.Messaging.Write(e.Error);
            }

            return library;
        }

        private Intermediate LinkPhase(IEnumerable<Intermediate> intermediates, IEnumerable<string> libraryFiles, ITupleDefinitionCreator creator)
        {
            var libraries = this.LoadLibraries(libraryFiles, creator);

            if (this.Messaging.EncounteredError)
            {
                return null;
            }

            var context = this.ServiceProvider.GetService<ILinkContext>();
            context.Extensions = this.ExtensionManager.Create<ILinkerExtension>();
            context.ExtensionData = this.ExtensionManager.Create<IExtensionData>();
            context.ExpectedOutputType = this.OutputType;
            context.Intermediates = intermediates.Concat(libraries).ToList();
            context.TupleDefinitionCreator = creator;

            var linker = this.ServiceProvider.GetService<ILinker>();
            return linker.Link(context);
        }

        private void BindPhase(Intermediate output, IEnumerable<Localization> localizations, IEnumerable<string> filterCultures, string cabCachePath, IEnumerable<BindPath> bindPaths)
        {
            var intermediateFolder = this.IntermediateFolder;
            if (String.IsNullOrEmpty(intermediateFolder))
            {
                intermediateFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            }

            ResolveResult resolveResult;
            {
                var context = this.ServiceProvider.GetService<IResolveContext>();
                context.BindPaths = bindPaths;
                context.Extensions = this.ExtensionManager.Create<IResolverExtension>();
                context.ExtensionData = this.ExtensionManager.Create<IExtensionData>();
                context.FilterCultures = filterCultures;
                context.IntermediateFolder = intermediateFolder;
                context.IntermediateRepresentation = output;
                context.Localizations = localizations;
                context.VariableResolver = new WixVariableResolver(this.Messaging);

                var resolver = this.ServiceProvider.GetService<IResolver>();
                resolveResult = resolver.Resolve(context);
            }

            if (this.Messaging.EncounteredError)
            {
                return;
            }

            BindResult bindResult;
            {
                var context = this.ServiceProvider.GetService<IBindContext>();
                //context.CabbingThreadCount = this.CabbingThreadCount;
                context.CabCachePath = cabCachePath;
                context.Codepage = resolveResult.Codepage;
                //context.DefaultCompressionLevel = this.DefaultCompressionLevel;
                context.DelayedFields = resolveResult.DelayedFields;
                context.ExpectedEmbeddedFiles = resolveResult.ExpectedEmbeddedFiles;
                context.Extensions = this.ExtensionManager.Create<IBinderExtension>();
                context.Ices = Array.Empty<string>(); // TODO: set this correctly
                context.IntermediateFolder = intermediateFolder;
                context.IntermediateRepresentation = resolveResult.IntermediateRepresentation;
                context.OutputPath = this.OutputFile;
                context.OutputPdbPath = Path.ChangeExtension(this.OutputFile, ".wixpdb");
                context.SuppressIces = Array.Empty<string>(); // TODO: set this correctly
                context.SuppressValidation = true; // TODO: set this correctly

                var binder = this.ServiceProvider.GetService<IBinder>();
                bindResult = binder.Bind(context);
            }

            if (this.Messaging.EncounteredError)
            {
                return;
            }

            {
                var context = this.ServiceProvider.GetService<ILayoutContext>();
                context.Extensions = this.ExtensionManager.Create<ILayoutExtension>();
                context.TrackedFiles = bindResult.TrackedFiles;
                context.FileTransfers = bindResult.FileTransfers;
                context.IntermediateFolder = intermediateFolder;
                context.ContentsFile = this.ContentsFile;
                context.OutputsFile = this.OutputsFile;
                context.BuiltOutputsFile = this.BuiltOutputsFile;
                context.SuppressAclReset = false; // TODO: correctly set SuppressAclReset

                var layout = this.ServiceProvider.GetService<ILayoutCreator>();
                layout.Layout(context);
            }
        }

        private IEnumerable<Intermediate> LoadLibraries(IEnumerable<string> libraryFiles, ITupleDefinitionCreator creator)
        {
            try
            {
                return Intermediate.Load(libraryFiles, creator);
            }
            catch (WixCorruptFileException e)
            {
                this.Messaging.Write(e.Error);
            }
            catch (WixUnexpectedFileFormatException e)
            {
                this.Messaging.Write(e.Error);
            }

            return Array.Empty<Intermediate>();
        }

        private IEnumerable<Localization> LoadLocalizationFiles(IEnumerable<string> locFiles, IDictionary<string, string> preprocessorVariables)
        {
            var localizations = new List<Localization>();
            var localizer = this.ServiceProvider.GetService<ILocalizer>();

            foreach (var loc in locFiles)
            {
                var document = this.Preprocess(preprocessorVariables, loc);

                if (this.Messaging.EncounteredError)
                {
                    continue;
                }

                var localization = localizer.ParseLocalizationFile(document);
                localizations.Add(localization);
            }

            return localizations;
        }

        private XDocument Preprocess(IDictionary<string, string> preprocessorVariables, string sourcePath)
        {
            var context = this.ServiceProvider.GetService<IPreprocessContext>();
            context.Extensions = this.ExtensionManager.Create<IPreprocessorExtension>();
            context.Platform = this.Platform;
            context.IncludeSearchPaths = this.IncludeSearchPaths;
            context.SourcePath = sourcePath;
            context.Variables = preprocessorVariables;

            XDocument document = null;
            try
            {
                var preprocessor = this.ServiceProvider.GetService<IPreprocessor>();
                document = preprocessor.Preprocess(context);
            }
            catch (WixException e)
            {
                this.Messaging.Write(e.Error);
            }

            return document;
        }

        private class CommandLine
        {
            private static readonly char[] BindPathSplit = { '=' };

            public bool BindFiles { get; private set; }

            public List<BindPath> BindPaths { get; } = new List<BindPath>();

            public string CabCachePath { get; private set; }

            public List<string> Cultures { get; } = new List<string>();

            public List<string> Defines { get; } = new List<string>();

            public List<string> IncludeSearchPaths { get; } = new List<string>();

            public List<string> LocalizationFilePaths { get; } = new List<string>();

            public List<string> LibraryFilePaths { get; } = new List<string>();

            public List<string> SourceFilePaths { get; } = new List<string>();

            public Platform Platform { get; private set; }

            public bool ShowLogo { get; private set; }

            public bool ShowHelp { get; private set; }

            public string IntermediateFolder { get; private set; }

            public string OutputFile { get; private set; }

            public string OutputType { get; private set; }

            public string ContentsFile { get; private set; }

            public string OutputsFile { get; private set; }

            public string BuiltOutputsFile { get; private set; }

            public CommandLine(IMessaging messaging)
            {
                this.Messaging = messaging;
            }

            private IMessaging Messaging { get; }

            public bool TryParseArgument(string arg, ICommandLineParser parser)
            {
                if (parser.IsSwitch(arg))
                {
                    var parameter = arg.Substring(1);
                    switch (parameter.ToLowerInvariant())
                    {
                    case "?":
                    case "h":
                    case "help":
                        this.ShowHelp = true;
                        return true;

                    case "arch":
                    case "platform":
                    {
                        var value = parser.GetNextArgumentOrError(arg);
                        if (Enum.TryParse(value, true, out Platform platform))
                        {
                            this.Platform = platform;
                            return true;
                        }
                        break;
                    }

                    case "bindfiles":
                        this.BindFiles = true;
                        return true;

                    case "bindpath":
                    {
                        var value = parser.GetNextArgumentOrError(arg);
                        if (this.TryParseBindPath(value, out var bindPath))
                        {
                            this.BindPaths.Add(bindPath);
                            return true;
                        }
                        break;
                    }
                    case "cc":
                        this.CabCachePath = parser.GetNextArgumentOrError(arg);
                        return true;

                    case "culture":
                        parser.GetNextArgumentOrError(arg, this.Cultures);
                        return true;

                    case "contentsfile":
                        this.ContentsFile = parser.GetNextArgumentAsFilePathOrError(arg);
                        return true;
                    case "outputsfile":
                        this.OutputsFile = parser.GetNextArgumentAsFilePathOrError(arg);
                        return true;
                    case "builtoutputsfile":
                        this.BuiltOutputsFile = parser.GetNextArgumentAsFilePathOrError(arg);
                        return true;

                    case "d":
                    case "define":
                        parser.GetNextArgumentOrError(arg, this.Defines);
                        return true;

                    case "i":
                    case "includepath":
                        parser.GetNextArgumentOrError(arg, this.IncludeSearchPaths);
                        return true;

                    case "intermediatefolder":
                        this.IntermediateFolder = parser.GetNextArgumentAsDirectoryOrError(arg);
                        return true;

                    case "loc":
                        parser.GetNextArgumentAsFilePathOrError(arg, "localization files", this.LocalizationFilePaths);
                        return true;

                    case "lib":
                        parser.GetNextArgumentAsFilePathOrError(arg, "library files", this.LibraryFilePaths);
                        return true;

                    case "o":
                    case "out":
                        this.OutputFile = parser.GetNextArgumentAsFilePathOrError(arg);
                        return true;

                    case "outputtype":
                        this.OutputType = parser.GetNextArgumentOrError(arg);
                        return true;

                    case "nologo":
                        this.ShowLogo = false;
                        return true;

                    case "v":
                    case "verbose":
                        this.Messaging.ShowVerboseMessages = true;
                        return true;

                    case "sval":
                        // todo: implement
                        return true;

                    case "sw":
                    case "suppresswarning":
                        var warning = parser.GetNextArgumentOrError(arg);
                        if (!String.IsNullOrEmpty(warning))
                        {
                            var warningNumber = Convert.ToInt32(warning);
                            this.Messaging.SuppressWarningMessage(warningNumber);
                        }
                        return true;
                    }

                    return false;
                }
                else
                {
                    parser.GetArgumentAsFilePathOrError(arg, "source code", this.SourceFilePaths);
                    return true;
                }
            }

            public string CalculateIntermedateFolder()
            {
                return String.IsNullOrEmpty(this.IntermediateFolder) ? Path.GetTempPath() : this.IntermediateFolder;
            }

            public OutputType CalculateOutputType()
            {
                if (String.IsNullOrEmpty(this.OutputType))
                {
                    this.OutputType = Path.GetExtension(this.OutputFile);
                }

                switch (this.OutputType.ToLowerInvariant())
                {
                case "bundle":
                case ".exe":
                    return Data.OutputType.Bundle;

                case "library":
                case ".wixlib":
                    return Data.OutputType.Library;

                case "module":
                case ".msm":
                    return Data.OutputType.Module;

                case "patch":
                case ".msp":
                    return Data.OutputType.Patch;

                case ".pcp":
                    return Data.OutputType.PatchCreation;

                case "product":
                case "package":
                case ".msi":
                    return Data.OutputType.Product;

                case "transform":
                case ".mst":
                    return Data.OutputType.Transform;

                case "intermediatepostlink":
                case ".wixipl":
                    return Data.OutputType.IntermediatePostLink;
                }

                return Data.OutputType.Unknown;
            }

            public IEnumerable<string> CalculateFilterCultures()
            {
                var result = new List<string>();

                if (this.Cultures == null)
                {
                }
                else if (this.Cultures.Count == 1 && this.Cultures[0].Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    // When null is used treat it as if cultures wasn't specified. This is
                    // needed for batching in the MSBuild task since MSBuild doesn't support
                    // empty items.
                }
                else
                {
                    foreach (var culture in this.Cultures)
                    {
                        // Neutral is different from null. For neutral we still want to do culture filtering.
                        // Set the culture to the empty string = identifier for the invariant culture.
                        var filter = (culture.Equals("neutral", StringComparison.OrdinalIgnoreCase)) ? String.Empty : culture;
                        result.Add(filter);
                    }
                }

                return result;
            }

            public IDictionary<string, string> GatherPreprocessorVariables()
            {
                var variables = new Dictionary<string, string>();

                foreach (var pair in this.Defines)
                {
                    var value = pair.Split(new[] { '=' }, 2);

                    if (variables.ContainsKey(value[0]))
                    {
                        this.Messaging.Write(ErrorMessages.DuplicateVariableDefinition(value[0], (1 == value.Length) ? String.Empty : value[1], variables[value[0]]));
                        continue;
                    }

                    variables.Add(value[0], (1 == value.Length) ? String.Empty : value[1]);
                }

                return variables;
            }


            public IEnumerable<SourceFile> GatherSourceFiles(string intermediateDirectory)
            {
                var files = new List<SourceFile>();

                foreach (var item in this.SourceFilePaths)
                {
                    var sourcePath = item;
                    var outputPath = Path.Combine(intermediateDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".wir");

                    files.Add(new SourceFile(sourcePath, outputPath));
                }

                return files;
            }

            private bool TryParseBindPath(string bindPath, out BindPath bp)
            {
                var namedPath = bindPath.Split(BindPathSplit, 2);
                bp = (1 == namedPath.Length) ? new BindPath(namedPath[0]) : new BindPath(namedPath[0], namedPath[1]);

                if (File.Exists(bp.Path))
                {
                    this.Messaging.Write(ErrorMessages.ExpectedDirectoryGotFile("-bindpath", bp.Path));
                    return false;
                }

                return true;
            }
        }
    }
}
