// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem
{
    // Internal tracker for DefaultProjectSnapshot
    internal class ProjectState
    {
        private const ProjectDifference ClearComputedStateMask = ProjectDifference.ConfigurationChanged;

        private const ProjectDifference ClearCachedTagHelpersMask =
            ProjectDifference.ConfigurationChanged |
            ProjectDifference.TagHelpersChanged;

        private const ProjectDifference ClearDocumentCollectionVersionMask =
            ProjectDifference.ConfigurationChanged |
            ProjectDifference.DocumentAdded |
            ProjectDifference.DocumentRemoved;

        private static readonly ImmutableDictionary<string, DocumentState> EmptyDocuments = ImmutableDictionary.Create<string, DocumentState>(FilePathComparer.Instance);
        private static readonly ImmutableDictionary<string, ImmutableArray<string>> EmptyImportsToRelatedDocuments = ImmutableDictionary.Create<string, ImmutableArray<string>>(FilePathComparer.Instance);
        private static readonly IReadOnlyList<TagHelperDescriptor> EmptyTagHelpers = Array.Empty<TagHelperDescriptor>();
        private readonly object _lock;

        private ComputedStateTracker _computedState;

        public static ProjectState Create(
            HostWorkspaceServices services,
            HostProject hostProject,
            IReadOnlyList<TagHelperDescriptor> tagHelpers = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            tagHelpers = tagHelpers ?? EmptyTagHelpers;

            return new ProjectState(services, hostProject, tagHelpers);
        }

        private ProjectState(
            HostWorkspaceServices services,
            HostProject hostProject,
            IReadOnlyList<TagHelperDescriptor> tagHelpers)
        {
            Services = services;
            HostProject = hostProject;
            TagHelpers = tagHelpers;
            Documents = EmptyDocuments;
            ImportsToRelatedDocuments = EmptyImportsToRelatedDocuments;
            Version = VersionStamp.Create();
            DocumentCollectionVersion = Version;

            _lock = new object();
        }

        private ProjectState(
            ProjectState older,
            ProjectDifference difference,
            HostProject hostProject,
            IReadOnlyList<TagHelperDescriptor> tagHelpers,
            ImmutableDictionary<string, DocumentState> documents,
            ImmutableDictionary<string, ImmutableArray<string>> importsToRelatedDocuments)
        {
            if (older == null)
            {
                throw new ArgumentNullException(nameof(older));
            }

            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            if (documents == null)
            {
                throw new ArgumentNullException(nameof(documents));
            }

            if (importsToRelatedDocuments == null)
            {
                throw new ArgumentNullException(nameof(importsToRelatedDocuments));
            }

            Services = older.Services;
            Version = older.Version.GetNewerVersion();

            HostProject = hostProject;
            TagHelpers = tagHelpers;
            Documents = documents;
            ImportsToRelatedDocuments = importsToRelatedDocuments;

            _lock = new object();

            if ((difference & ClearDocumentCollectionVersionMask) == 0)
            {
                // Document collection hasn't changed
                DocumentCollectionVersion = older.DocumentCollectionVersion;
            }
            else
            {
                DocumentCollectionVersion = Version;
            }

            if ((difference & ClearComputedStateMask) == 0 && older._computedState != null)
            {
                // Optimistically cache the RazorProjectEngine.
                _computedState = new ComputedStateTracker(this, older._computedState);
            }

            if ((difference & ClearCachedTagHelpersMask) == 0 && _computedState != null)
            {
                // It's OK to keep the computed Tag Helpers.
                _computedState.TaskUnsafe = older._computedState?.TaskUnsafe;
            }
        }

        // Internal set for testing.
        public ImmutableDictionary<string, DocumentState> Documents { get; internal set; }

        // Internal set for testing.
        public ImmutableDictionary<string, ImmutableArray<string>> ImportsToRelatedDocuments { get; internal set; }

        public HostProject HostProject { get; }

        public HostWorkspaceServices Services { get; }

        public IReadOnlyList<TagHelperDescriptor> TagHelpers { get; }

        /// <summary>
        /// Gets the version of this project, INCLUDING content changes. The <see cref="Version"/> is
        /// incremented for each new <see cref="ProjectState"/> instance created.
        /// </summary>
        public VersionStamp Version { get; }

        /// <summary>
        /// Gets the version of this project, NOT INCLUDING computed or content changes. The
        /// <see cref="DocumentCollectionVersion"/> is incremented each time the configuration changes or
        /// a document is added or removed.
        /// </summary>
        public VersionStamp DocumentCollectionVersion { get; }

        public RazorProjectEngine ProjectEngine => ComputedState.ProjectEngine;

        public bool IsTagHelperResultAvailable => ComputedState.TaskUnsafe?.IsCompleted == true;

        private ComputedStateTracker ComputedState
        {
            get
            {
                if (_computedState == null)
                {
                    lock (_lock)
                    {
                        if (_computedState == null)
                        {
                            _computedState = new ComputedStateTracker(this);
                        }
                    }
                }

                return _computedState;
            }
        }

        /// <summary>
        /// Gets the version of this project based on the computed state, NOT INCLUDING content
        /// changes. The computed state is guaranteed to change when the configuration or tag helpers
        /// change.
        /// </summary>
        /// <returns>Asynchronously returns the computed version.</returns>
        public VersionStamp GetComputedStateVersion()
        {
            var version = ComputedState.ProjectStateVersion;
            return version;
        }

        public ProjectState WithAddedHostDocument(HostDocument hostDocument, Func<Task<TextAndVersion>> loader)
        {
            if (hostDocument == null)
            {
                throw new ArgumentNullException(nameof(hostDocument));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            // Ignore attempts to 'add' a document with different data, we only
            // care about one, so it might as well be the one we have.
            if (Documents.ContainsKey(hostDocument.FilePath))
            {
                return this;
            }

            var documents = Documents.Add(hostDocument.FilePath, DocumentState.Create(Services, hostDocument, loader));

            // Compute the effect on the import map
            var importTargetPaths = GetImportDocumentTargetPaths(hostDocument.TargetPath);
            var importsToRelatedDocuments = AddToImportsToRelatedDocuments(ImportsToRelatedDocuments, hostDocument, importTargetPaths);

            // Now check if the updated document is an import - it's important this this happens after
            // updating the imports map.
            if (importsToRelatedDocuments.TryGetValue(hostDocument.TargetPath, out var relatedDocuments))
            {
                foreach (var relatedDocument in relatedDocuments)
                {
                    documents = documents.SetItem(relatedDocument, documents[relatedDocument].WithImportsChange());
                }
            }

            var state = new ProjectState(this, ProjectDifference.DocumentAdded, HostProject, TagHelpers, documents, importsToRelatedDocuments);
            return state;
        }

        public ProjectState WithRemovedHostDocument(HostDocument hostDocument)
        {
            if (hostDocument == null)
            {
                throw new ArgumentNullException(nameof(hostDocument));
            }

            if (!Documents.ContainsKey(hostDocument.FilePath))
            {
                return this;
            }

            var documents = Documents.Remove(hostDocument.FilePath);

            // First check if the updated document is an import - it's important that this happens
            // before updating the imports map.
            if (ImportsToRelatedDocuments.TryGetValue(hostDocument.TargetPath, out var relatedDocuments))
            {
                foreach (var relatedDocument in relatedDocuments)
                {
                    documents = documents.SetItem(relatedDocument, documents[relatedDocument].WithImportsChange());
                }
            }

            // Compute the effect on the import map
            var importTargetPaths = GetImportDocumentTargetPaths(hostDocument.TargetPath);
            var importsToRelatedDocuments = RemoveFromImportsToRelatedDocuments(ImportsToRelatedDocuments, hostDocument, importTargetPaths);

            var state = new ProjectState(this, ProjectDifference.DocumentRemoved, HostProject, TagHelpers, documents, importsToRelatedDocuments);
            return state;
        }

        public ProjectState WithChangedHostDocument(HostDocument hostDocument, SourceText sourceText, VersionStamp version)
        {
            if (hostDocument == null)
            {
                throw new ArgumentNullException(nameof(hostDocument));
            }

            if (!Documents.TryGetValue(hostDocument.FilePath, out var document))
            {
                return this;
            }

            var documents = Documents.SetItem(hostDocument.FilePath, document.WithText(sourceText, version));

            if (ImportsToRelatedDocuments.TryGetValue(hostDocument.TargetPath, out var relatedDocuments))
            {
                foreach (var relatedDocument in relatedDocuments)
                {
                    documents = documents.SetItem(relatedDocument, documents[relatedDocument].WithImportsChange());
                }
            }

            var state = new ProjectState(this, ProjectDifference.DocumentChanged, HostProject, TagHelpers, documents, ImportsToRelatedDocuments);
            return state;
        }

        public ProjectState WithChangedHostDocument(HostDocument hostDocument, Func<Task<TextAndVersion>> loader)
        {
            if (hostDocument == null)
            {
                throw new ArgumentNullException(nameof(hostDocument));
            }

            if (!Documents.TryGetValue(hostDocument.FilePath, out var document))
            {
                return this;
            }

            var documents = Documents.SetItem(hostDocument.FilePath, document.WithTextLoader(loader));

            if (ImportsToRelatedDocuments.TryGetValue(hostDocument.TargetPath, out var relatedDocuments))
            {
                foreach (var relatedDocument in relatedDocuments)
                {
                    documents = documents.SetItem(relatedDocument, documents[relatedDocument].WithImportsChange());
                }
            }

            var state = new ProjectState(this, ProjectDifference.DocumentChanged, HostProject, TagHelpers, documents, ImportsToRelatedDocuments);
            return state;
        }

        public ProjectState WithHostProject(HostProject hostProject)
        {
            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            if (HostProject.Configuration.Equals(hostProject.Configuration))
            {
                return this;
            }

            var documents = Documents.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.WithConfigurationChange(), FilePathComparer.Instance);

            // If the host project has changed then we need to recompute the imports map
            var importsToRelatedDocuments = EmptyImportsToRelatedDocuments;

            foreach (var document in documents)
            {
                var importTargetPaths = GetImportDocumentTargetPaths(document.Value.HostDocument.TargetPath);
                importsToRelatedDocuments = AddToImportsToRelatedDocuments(ImportsToRelatedDocuments, document.Value.HostDocument, importTargetPaths);
            }

            var state = new ProjectState(this, ProjectDifference.ConfigurationChanged, hostProject, TagHelpers, documents, importsToRelatedDocuments);
            return state;
        }

        public ProjectState WithChangedTagHelpers(IReadOnlyList<TagHelperDescriptor> tagHelpers)
        {
            var difference = ProjectDifference.TagHelpersChanged;

            var documents = Documents.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.WithChangedTagHelpers(), FilePathComparer.Instance);
            var state = new ProjectState(this, difference, HostProject, tagHelpers, documents, ImportsToRelatedDocuments);
            return state;
        }

        private static ImmutableDictionary<string, ImmutableArray<string>> AddToImportsToRelatedDocuments(
            ImmutableDictionary<string, ImmutableArray<string>> importsToRelatedDocuments,
            HostDocument hostDocument,
            List<string> importTargetPaths)
        {
            foreach (var importTargetPath in importTargetPaths)
            {
                if (!importsToRelatedDocuments.TryGetValue(importTargetPath, out var relatedDocuments))
                {
                    relatedDocuments = ImmutableArray.Create<string>();
                }

                relatedDocuments = relatedDocuments.Add(hostDocument.FilePath);
                importsToRelatedDocuments = importsToRelatedDocuments.SetItem(importTargetPath, relatedDocuments);
            }

            return importsToRelatedDocuments;
        }

        private static ImmutableDictionary<string, ImmutableArray<string>> RemoveFromImportsToRelatedDocuments(
            ImmutableDictionary<string, ImmutableArray<string>> importsToRelatedDocuments,
            HostDocument hostDocument,
            List<string> importTargetPaths)
        {
            foreach (var importTargetPath in importTargetPaths)
            {
                if (importsToRelatedDocuments.TryGetValue(importTargetPath, out var relatedDocuments))
                {
                    relatedDocuments = relatedDocuments.Remove(hostDocument.FilePath);
                    if (relatedDocuments.Length > 0)
                    {
                        importsToRelatedDocuments = importsToRelatedDocuments.SetItem(importTargetPath, relatedDocuments);
                    }
                    else
                    {
                        importsToRelatedDocuments = importsToRelatedDocuments.Remove(importTargetPath);
                    }
                }
            }

            return importsToRelatedDocuments;
        }

        private RazorProjectEngine CreateProjectEngine()
        {
            var factory = Services.GetRequiredService<ProjectSnapshotProjectEngineFactory>();
            return factory.Create(HostProject.Configuration, Path.GetDirectoryName(HostProject.FilePath), configure: null);
        }

        public List<string> GetImportDocumentTargetPaths(string targetPath)
        {
            var projectEngine = ComputedState.ProjectEngine;
            var importFeatures = projectEngine.ProjectFeatures.OfType<IImportProjectFeature>();
            var projectItem = projectEngine.FileSystem.GetItem(targetPath);
            var importItems = importFeatures.SelectMany(f => f.GetImports(projectItem)).Where(i => i.FilePath != null);

            // Target path looks like `Foo\\Bar.cshtml`
            var targetPaths = new List<string>();
            foreach (var importItem in importItems)
            {
                var itemTargetPath = importItem.FilePath.Replace('/', '\\').TrimStart('\\');
                targetPaths.Add(itemTargetPath);
            }

            return targetPaths;
        }

        // ComputedStateTracker is the 'holder' of all of the state that can be cached based on
        // the data in a ProjectState. It should not hold onto a ProjectState directly
        // as that could lead to things being in memory longer than we want them to.
        //
        // Rather, a ComputedStateTracker instance can hold on to a previous instance from an older
        // version of the same project. 
        private class ComputedStateTracker
        {
            // ProjectState.Version 
            private readonly VersionStamp _projectStateVersion;
            private readonly object _lock;

            private ComputedStateTracker _older; // We be set to null when state is computed
            public Task<(IReadOnlyList<TagHelperDescriptor>, VersionStamp)> TaskUnsafe;

            public ComputedStateTracker(ProjectState state, ComputedStateTracker older = null)
            {
                _projectStateVersion = state.Version;
                _lock = state._lock;
                _older = older;

                ProjectEngine = _older?.ProjectEngine;
                if (ProjectEngine == null)
                {
                    ProjectEngine = state.CreateProjectEngine();
                }
            }

            public RazorProjectEngine ProjectEngine { get; }

            public VersionStamp ProjectStateVersion => _projectStateVersion;
        }
    }
}
