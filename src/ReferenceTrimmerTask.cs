using System.Reflection;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace ReferenceTrimmer
{
    public sealed class ReferenceTrimmerTask : MSBuildTask
    {
        private static readonly HashSet<string> NugetAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            // Direct dependency
            "NuGet.ProjectModel",

            // Indirect dependencies
            "NuGet.Common",
            "NuGet.Frameworks",
            "NuGet.Packaging",
            "NuGet.Versioning",
        };

        [Required]
        public string MSBuildProjectFile { get; set; }

        public ITaskItem[] UsedReferences { get; set; }

        public ITaskItem[] References { get; set; }

        public ITaskItem[] ProjectReferences { get; set; }

        public ITaskItem[] PackageReferences { get; set; }

        public string ProjectAssetsFile { get; set; }

        public string NuGetTargetMoniker { get; set; }

        public string RuntimeIdentifier { get; set; }

        public string NuGetRestoreTargets { get; set; }

        public ITaskItem[] TargetFrameworkDirectories { get; set; }

        public override bool Execute()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            try
            {
                HashSet<string> assemblyReferences = GetAssemblyReferences();
                Dictionary<string, PackageInfo> packageInfos = GetPackageInfos();
                HashSet<string> targetFrameworkAssemblies = GetTargetFrameworkAssemblyNames();

                if (References != null)
                {
                    foreach (ITaskItem reference in References)
                    {
                        // Ignore implicity defined references (references which are SDK-provided)
                        if (reference.GetMetadata("IsImplicitlyDefined").Equals("true", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // During the _HandlePackageFileConflicts target (ResolvePackageFileConflicts task), assembly conflicts may be
                        // resolved with an assembly from the target framework instead of a package. The package may be an indirect dependency,
                        // so the resulting reference would be unavoidable.
                        if (targetFrameworkAssemblies.Contains(reference.ItemSpec))
                        {
                            continue;
                        }

                        // Ignore references from packages. Those as handled later.
                        if (reference.GetMetadata("NuGetPackageId").Length != 0)
                        {
                            continue;
                        }

                        var referenceSpec = reference.ItemSpec;
                        var referenceHintPath = reference.GetMetadata("HintPath");
                        var referenceName = reference.GetMetadata("Name");

                        string referenceAssemblyName;

                        if (!string.IsNullOrEmpty(referenceHintPath) && File.Exists(referenceHintPath))
                        {
                            // If a hint path is given and exists, use that assembly's name.
                            referenceAssemblyName = AssemblyName.GetAssemblyName(referenceHintPath).Name;
                        }
                        else if (!string.IsNullOrEmpty(referenceName) && File.Exists(referenceSpec))
                        {
                            // If a name is given and the spec is an existing file, use that assembly's name.
                            referenceAssemblyName = AssemblyName.GetAssemblyName(referenceSpec).Name;
                        }
                        else
                        {
                            // The assembly name is probably just the item spec.
                            referenceAssemblyName = referenceSpec;
                        }

                        if (!assemblyReferences.Contains(referenceAssemblyName))
                        {
                            LogWarning("RT0001", "Reference {0} can be removed", referenceSpec);
                        }
                    }
                }

                if (ProjectReferences != null)
                {
                    foreach (ITaskItem projectReference in ProjectReferences)
                    {
                        AssemblyName projectReferenceAssemblyName = new(projectReference.GetMetadata("FusionName"));
                        if (!assemblyReferences.Contains(projectReferenceAssemblyName.Name))
                        {
                            string referenceProjectFile = projectReference.GetMetadata("OriginalProjectReferenceItemSpec");
                            LogWarning("RT0002", "ProjectReference {0} can be removed", referenceProjectFile);
                        }
                    }
                }

                if (PackageReferences != null)
                {
                    foreach (ITaskItem packageReference in PackageReferences)
                    {
                        if (!packageInfos.TryGetValue(packageReference.ItemSpec, out PackageInfo packageInfo))
                        {
                            // These are likely Analyzers, tools, etc.
                            continue;
                        }

                        // Ignore packages with build logic as we cannot easily evaluate whether the build logic is necessary or not.
                        if (packageInfo.BuildFiles.Count > 0)
                        {
                            continue;
                        }

                        if (!packageInfo.CompileTimeAssemblies.Any(assemblyReferences.Contains))
                        {
                            LogWarning("RT0003", "PackageReference {0} can be removed", packageReference);
                        }
                    }
                }
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
            }

            return !Log.HasLoggedErrors;
        }

        private void LogWarning(string code, string message, params object[] messageArgs) => Log.LogWarning(null, code, null, MSBuildProjectFile, 0, 0, 0, 0, message, messageArgs);

        private HashSet<string> GetAssemblyReferences() => new(UsedReferences.Select(usedReference => AssemblyName.GetAssemblyName(usedReference.ItemSpec).Name), StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, PackageInfo> GetPackageInfos()
        {
            var packageInfoBuilders = new Dictionary<string, PackageInfoBuilder>(StringComparer.OrdinalIgnoreCase);

            var lockFile = LockFileUtilities.GetLockFile(ProjectAssetsFile, NullLogger.Instance);
            var packageFolders = lockFile.PackageFolders.Select(item => item.Path).ToList();

            var nugetTarget = lockFile.GetTarget(NuGetFramework.Parse(NuGetTargetMoniker), RuntimeIdentifier);
            var nugetLibraries = nugetTarget.Libraries
                .Where(nugetLibrary => nugetLibrary.Type.Equals("Package", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Compute the hierarchy of packages.
            // Keys are packages and values are packages which depend on that package.
            var nugetDependants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (LockFileTargetLibrary nugetLibrary in nugetLibraries)
            {
                var packageId = nugetLibrary.Name;
                foreach (var dependency in nugetLibrary.Dependencies)
                {
                    if (!nugetDependants.TryGetValue(dependency.Id, out var parents))
                    {
                        parents = new List<string>();
                        nugetDependants.Add(dependency.Id, parents);
                    }

                    parents.Add(packageId);
                }
            }

            // Get the transitive closure of assemblies included by each package
            foreach (LockFileTargetLibrary nugetLibrary in nugetLibraries)
            {
                string nugetLibraryRelativePath = lockFile.GetLibrary(nugetLibrary.Name, nugetLibrary.Version).Path;
                string nugetLibraryAbsolutePath = packageFolders
                    .Select(packageFolder => Path.Combine(packageFolder, nugetLibraryRelativePath))
                    .First(Directory.Exists);

                List<string> nugetLibraryAssemblies = nugetLibrary.CompileTimeAssemblies
                    .Select(item => item.Path)
                    .Where(path => !path.EndsWith("_._", StringComparison.Ordinal)) // Ignore special packages
                    .Select(path =>
                    {
                        var fullPath = Path.Combine(nugetLibraryAbsolutePath, path);
                        return AssemblyName.GetAssemblyName(fullPath).Name;
                    })
                    .ToList();

                List<string> buildFiles = nugetLibrary.Build
                    .Select(item => Path.Combine(nugetLibraryAbsolutePath, item.Path))
                    .ToList();

                // Add this package's assets, if there are any
                if (nugetLibraryAssemblies.Count > 0 || buildFiles.Count > 0)
                {
                    // Walk up to add assets to all packages which directly or indirectly depend on this one.
                    var seenDependants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var queue = new Queue<string>();
                    queue.Enqueue(nugetLibrary.Name);
                    while (queue.Count > 0)
                    {
                        var packageId = queue.Dequeue();

                        if (!packageInfoBuilders.TryGetValue(packageId, out PackageInfoBuilder packageInfoBuilder))
                        {
                            packageInfoBuilder = new PackageInfoBuilder();
                            packageInfoBuilders.Add(packageId, packageInfoBuilder);
                        }

                        packageInfoBuilder.AddCompileTimeAssemblies(nugetLibraryAssemblies);
                        packageInfoBuilder.AddBuildFiles(buildFiles);

                        // Recurse though dependants
                        if (nugetDependants.TryGetValue(packageId, out var dependants))
                        {
                            foreach (var dependant in dependants)
                            {
                                if (seenDependants.Add(dependant))
                                {
                                    queue.Enqueue(dependant);
                                }
                            }
                        }
                    }
                }
            }

            // Create the final collection
            var packageInfos = new Dictionary<string, PackageInfo>(packageInfoBuilders.Count, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PackageInfoBuilder> packageInfoBuilder in packageInfoBuilders)
            {
                packageInfos.Add(packageInfoBuilder.Key, packageInfoBuilder.Value.ToPackageInfo());
            }

            return packageInfos;
        }

        internal HashSet<string> GetTargetFrameworkAssemblyNames()
        {
            HashSet<string> targetFrameworkAssemblyNames = new();

            // This follows the same logic as FrameworkListReader.
            // See: https://github.com/dotnet/sdk/blob/main/src/Tasks/Common/ConflictResolution/FrameworkListReader.cs
            if (TargetFrameworkDirectories != null)
            {
                foreach (ITaskItem targetFrameworkDirectory in TargetFrameworkDirectories)
                {
                    string frameworkListPath = Path.Combine(targetFrameworkDirectory.ItemSpec, "RedistList", "FrameworkList.xml");
                    if (!File.Exists(frameworkListPath))
                    {
                        continue;
                    }

                    XDocument frameworkList = XDocument.Load(frameworkListPath);
                    foreach (XElement file in frameworkList.Root.Elements("File"))
                    {
                        string type = file.Attribute("Type")?.Value;
                        if (type?.Equals("Analyzer", StringComparison.OrdinalIgnoreCase) ?? false)
                        {
                            continue;
                        }

                        string assemblyName = file.Attribute("AssemblyName")?.Value;
                        if (!string.IsNullOrEmpty(assemblyName))
                        {
                            targetFrameworkAssemblyNames.Add(assemblyName);
                        }
                    }
                }
            }

            return targetFrameworkAssemblyNames;
        }

        /// <summary>
        /// Assembly resolution needed for parsing the lock file, needed if the version the task depends on is a different version than MSBuild's
        /// </summary>
        private Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            AssemblyName assemblyName = new(args.Name);

            if (NugetAssemblies.Contains(assemblyName.Name))
            {
                string nugetProjectModelFile = Path.Combine(Path.GetDirectoryName(NuGetRestoreTargets), assemblyName.Name + ".dll");
                if (File.Exists(nugetProjectModelFile))
                {
                    return Assembly.LoadFrom(nugetProjectModelFile);
                }
            }

            return null;
        }

        private sealed class PackageInfoBuilder
        {
            private List<string> _compileTimeAssemblies;

            private List<string> _buildFiles;

            public void AddCompileTimeAssemblies(List<string> compileTimeAssemblies)
            {
                if (compileTimeAssemblies.Count == 0)
                {
                    return;
                }

                _compileTimeAssemblies ??= new(compileTimeAssemblies.Count);
                _compileTimeAssemblies.AddRange(compileTimeAssemblies);
            }

            public void AddBuildFiles(List<string> buildFiles)
            {
                if (buildFiles.Count == 0)
                {
                    return;
                }

                _buildFiles ??= new(buildFiles.Count);
                _buildFiles.AddRange(buildFiles);
            }

            public PackageInfo ToPackageInfo()
                => new(
                    (IReadOnlyCollection<string>)_compileTimeAssemblies ?? Array.Empty<string>(),
                    (IReadOnlyCollection<string>)_buildFiles ?? Array.Empty<string>());
        }

        private readonly record struct PackageInfo(
            IReadOnlyCollection<string> CompileTimeAssemblies,
            IReadOnlyCollection<string> BuildFiles);
    }
}
