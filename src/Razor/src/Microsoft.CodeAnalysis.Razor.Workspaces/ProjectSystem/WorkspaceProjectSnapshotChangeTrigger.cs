// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem
{
    [Shared]
    [Export(typeof(ProjectSnapshotChangeTrigger))]
    internal class WorkspaceProjectSnapshotChangeTrigger : ProjectSnapshotChangeTrigger
    {
        private readonly ProjectWorkspaceStateGenerator _workspaceStateGenerator;
        private ProjectSnapshotManagerBase _projectManager;

        public int EnqueueDelay { get; set; } = 3 * 1000;

        // We throttle updates to projects to prevent doing too much work while the projects
        // are being initialized.
        //
        // Internal for testing
        internal Dictionary<ProjectId, Task> _deferredUpdates;

        [ImportingConstructor]
        public WorkspaceProjectSnapshotChangeTrigger(ProjectWorkspaceStateGenerator workspaceStateGenerator)
        {
            if (workspaceStateGenerator == null)
            {
                throw new ArgumentNullException(nameof(workspaceStateGenerator));
            }

            _workspaceStateGenerator = workspaceStateGenerator;
        }

        public override void Initialize(ProjectSnapshotManagerBase projectManager)
        {
            _projectManager = projectManager;
            _projectManager.Changed += ProjectManager_Changed;
            _projectManager.Workspace.WorkspaceChanged += Workspace_WorkspaceChanged;

            _deferredUpdates = new Dictionary<ProjectId, Task>();

            // This will usually no-op, in the case that another project snapshot change trigger immediately adds projects we want to be able to handle those projects
            InitializeSolution(_projectManager.Workspace.CurrentSolution);
        }

        // Internal for testing
        internal void Workspace_WorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            Project project;
            switch (e.Kind)
            {
                case WorkspaceChangeKind.ProjectAdded:
                    {
                        project = e.NewSolution.GetProject(e.ProjectId);

                        Debug.Assert(project != null);

                        if (TryGetProjectSnapshot(project.FilePath, out var projectSnapshot))
                        {
                            _workspaceStateGenerator.Enqueue(project, projectSnapshot);
                        }
                        break;
                    }

                case WorkspaceChangeKind.ProjectChanged:
                case WorkspaceChangeKind.ProjectReloaded:
                    {
                        project = e.NewSolution.GetProject(e.ProjectId);

                        if (TryGetProjectSnapshot(project?.FilePath, out var _))
                        {
                            EnqueueWithDelay(e.ProjectId);
                        }
                        break;
                    }

                case WorkspaceChangeKind.ProjectRemoved:
                    {
                        project = e.OldSolution.GetProject(e.ProjectId);
                        Debug.Assert(project != null);

                        if (TryGetProjectSnapshot(project?.FilePath, out var projectSnapshot))
                        {
                            // Roslyn workspace project was removed, need to clear TagHelpers.
                            var state = new ProjectWorkspaceState(Array.Empty<TagHelperDescriptor>());
                            _workspaceStateGenerator.Enqueue(project: null, projectSnapshot);
                        }

                        break;
                    }

                case WorkspaceChangeKind.DocumentChanged:
                case WorkspaceChangeKind.DocumentReloaded:
                    {
                        // This is the case when a component declaration file changes on disk. We have an MSBuild
                        // generator configured by the SDK that will poke these files on disk when a component
                        // is saved, or loses focus in the editor.
                        project = e.OldSolution.GetProject(e.ProjectId);
                        var document = project.GetDocument(e.DocumentId);

                        // Using EndsWith because Path.GetExtension will ignore everything before .cs
                        // Using Ordinal because the SDK generates these filenames.
                        if (document.FilePath != null && document.FilePath.EndsWith(".cshtml.g.cs", StringComparison.Ordinal))
                        {
                            EnqueueWithDelay(e.ProjectId);
                        }

                        break;
                    }

                case WorkspaceChangeKind.SolutionAdded:
                case WorkspaceChangeKind.SolutionChanged:
                case WorkspaceChangeKind.SolutionCleared:
                case WorkspaceChangeKind.SolutionReloaded:
                case WorkspaceChangeKind.SolutionRemoved:

                    if (e.OldSolution != null)
                    {
                        foreach (var p in e.OldSolution.Projects)
                        {

                            if (TryGetProjectSnapshot(p?.FilePath, out var _))
                            {
                                // Roslyn workspace project was removed, need to clear TagHelpers.
                                var state = new ProjectWorkspaceState(Array.Empty<TagHelperDescriptor>());
                                _projectManager.ProjectWorkspaceStateChanged(p.FilePath, state);
                            }
                        }
                    }

                    InitializeSolution(e.NewSolution);
                    break;
            }
        }

        private void InitializeSolution(Solution solution)
        {
            Debug.Assert(solution != null);

            foreach (var project in solution.Projects)
            {
                if (TryGetProjectSnapshot(project?.FilePath, out var projectSnapshot))
                {
                    _workspaceStateGenerator.Enqueue(project, projectSnapshot);
                }
            }
        }

        private void ProjectManager_Changed(object sender, ProjectChangeEventArgs args)
        {
            switch (args.Kind)
            {
                case ProjectChangeKind.ProjectAdded:
                    var associatedWorkspaceProject = _projectManager.Workspace.CurrentSolution.Projects.FirstOrDefault(
                        project => string.Equals(args.ProjectFilePath, project.FilePath, FilePathComparison.Instance));

                    if (associatedWorkspaceProject != null)
                    {
                        _workspaceStateGenerator.Enqueue(associatedWorkspaceProject, args.Newer);
                    }
                    break;
            }
        }

        private void EnqueueWithDelay(ProjectId projectId)
        {
            // A race is not possible here because we use the main thread to synchronize the updates
            // by capturing the sync context.
            if (!_deferredUpdates.TryGetValue(projectId, out var update) || update.IsCompleted)
            {
                _deferredUpdates[projectId] = EnqueAfterDelay(projectId);
            }
        }

        private async Task EnqueAfterDelay(ProjectId projectId)
        {
            await Task.Delay(EnqueueDelay);

            var solution = _projectManager.Workspace.CurrentSolution;
            var workspaceProject = solution.GetProject(projectId);
            if (workspaceProject != null && TryGetProjectSnapshot(workspaceProject.FilePath, out var projectSnapshot))
            {
                _workspaceStateGenerator.Enqueue(workspaceProject, projectSnapshot);
            }
        }

        private bool TryGetProjectSnapshot(string projectFilePath, out ProjectSnapshot projectSnapshot)
        {
            if (projectFilePath == null)
            {
                projectSnapshot = null;
                return false;
            }

            for (var i = 0; i < _projectManager.Projects.Count; i++)
            {
                var project = _projectManager.Projects[i];
                if (string.Equals(project.FilePath, projectFilePath, FilePathComparison.Instance))
                {
                    projectSnapshot = project;
                    return true;
                }
            }

            projectSnapshot = null;
            return false;
        }
    }
}
