// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.Extensions.Internal;

namespace Microsoft.CodeAnalysis.Razor
{
    [Shared]
    [Export(typeof(ProjectWorkspaceStateGenerator))]
    [Export(typeof(ProjectSnapshotChangeTrigger))]
    internal class ProjectWorkspaceStateGenerator : ProjectSnapshotChangeTrigger
    {
        private static readonly IReadOnlyList<TagHelperDescriptor> EmptyTagHelpers = Array.Empty<TagHelperDescriptor>();
        private readonly ForegroundDispatcher _foregroundDispatcher;
        private ProjectSnapshotManagerBase _projectManager;

        private readonly Dictionary<string, TagHelperResolutionRequest> _work;
        private Timer _timer;
        private TagHelperResolver _tagHelperResolver;

        [ImportingConstructor]
        public ProjectWorkspaceStateGenerator(ForegroundDispatcher foregroundDispatcher)
        {
            if (foregroundDispatcher == null)
            {
                throw new ArgumentNullException(nameof(foregroundDispatcher));
            }

            _foregroundDispatcher = foregroundDispatcher;

            _work = new Dictionary<string, TagHelperResolutionRequest>(FilePathComparer.Instance);
        }

        public bool HasPendingNotifications
        {
            get
            {
                lock (_work)
                {
                    return _work.Count > 0;
                }
            }
        }

        // Used in unit tests to control the timer delay.
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public bool IsScheduledOrRunning => _timer != null;

        // Used in unit tests to ensure we can control when background work starts.
        public ManualResetEventSlim BlockBackgroundWorkStart { get; set; }

        // Used in unit tests to ensure we can know when background work finishes.
        public ManualResetEventSlim NotifyBackgroundWorkStarting { get; set; }

        // Used in unit tests to ensure we can know when background has captured its current workload.
        public ManualResetEventSlim NotifyBackgroundCapturedWorkload { get; set; }

        // Used in unit tests to ensure we can control when background work completes.
        public ManualResetEventSlim BlockBackgroundWorkCompleting { get; set; }

        // Used in unit tests to ensure we can know when background work finishes.
        public ManualResetEventSlim NotifyBackgroundWorkCompleted { get; set; }

        private void OnStartingBackgroundWork()
        {
            if (BlockBackgroundWorkStart != null)
            {
                BlockBackgroundWorkStart.Wait();
                BlockBackgroundWorkStart.Reset();
            }

            if (NotifyBackgroundWorkStarting != null)
            {
                NotifyBackgroundWorkStarting.Set();
            }
        }

        private void OnCompletingBackgroundWork()
        {
            if (BlockBackgroundWorkCompleting != null)
            {
                BlockBackgroundWorkCompleting.Wait();
                BlockBackgroundWorkCompleting.Reset();
            }
        }

        private void OnCompletedBackgroundWork()
        {
            if (NotifyBackgroundWorkCompleted != null)
            {
                NotifyBackgroundWorkCompleted.Set();
            }
        }

        private void OnBackgroundCapturedWorkload()
        {
            if (NotifyBackgroundCapturedWorkload != null)
            {
                NotifyBackgroundCapturedWorkload.Set();
            }
        }

        public override void Initialize(ProjectSnapshotManagerBase projectManager)
        {
            if (projectManager == null)
            {
                throw new ArgumentNullException(nameof(projectManager));
            }

            _projectManager = projectManager;

            var razorLanguageServices = _projectManager.Workspace.Services.GetLanguageServices(RazorLanguage.Name);
            _tagHelperResolver = razorLanguageServices.GetRequiredService<TagHelperResolver>();
        }

        public void Enqueue(Project project, ProjectSnapshot projectSnapshot)
        {
            if (projectSnapshot == null)
            {
                throw new ArgumentNullException(nameof(projectSnapshot));
            }

            _foregroundDispatcher.AssertForegroundThread();

            lock (_work)
            {
                // We only want to store the last 'seen' version of any given document. That way when we pick one to process
                // it's always the best version to use.
                _work[projectSnapshot.FilePath] = new TagHelperResolutionRequest(project, projectSnapshot);

                StartWorker();
            }
        }

        protected virtual void StartWorker()
        {
            // Access to the timer is protected by the lock in Enqueue and in Timer_Tick
            if (_timer == null)
            {

                // Timer will fire after a fixed delay, but only once.
                _timer = NonCapturingTimer.Create(state => ((ProjectWorkspaceStateGenerator)state).Timer_Tick(), this, Delay, Timeout.InfiniteTimeSpan);
            }
        }

        private void Timer_Tick()
        {
            _ = TimerTick();
        }

        private async Task TimerTick()
        {
            try
            {
                _foregroundDispatcher.AssertBackgroundThread();

                // Timer is stopped.
                _timer.Change(Timeout.Infinite, Timeout.Infinite);

                OnStartingBackgroundWork();

                KeyValuePair<string, TagHelperResolutionRequest>[] work;
                lock (_work)
                {
                    work = _work.ToArray();
                    _work.Clear();
                }

                OnBackgroundCapturedWorkload();

                var workspaceStateChanges = new Dictionary<string, ProjectWorkspaceState>(_work.Count, FilePathComparer.Instance);
                for (var i = 0; i < work.Length; i++)
                {
                    var request = work[i].Value;
                    try
                    {
                        if (request.WorkspaceProject == null)
                        {
                            // No longer an associated workspace project
                            var workspaceState = new ProjectWorkspaceState(EmptyTagHelpers);
                            workspaceStateChanges[request.ProjectSnapshot.FilePath] = workspaceState;
                        }
                        else
                        {
                            var tagHelperResolutionResult = await _tagHelperResolver.GetTagHelpersAsync(request.WorkspaceProject, request.ProjectSnapshot);
                            var workspaceState = new ProjectWorkspaceState(tagHelperResolutionResult.Descriptors);
                            workspaceStateChanges[request.ProjectSnapshot.FilePath] = workspaceState;
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportError(request.ProjectSnapshot, ex);
                    }
                }

                await Task.Factory.StartNew(
                    (generator) => ((ProjectWorkspaceStateGenerator)generator).ReportWorkspaceStateChanges(workspaceStateChanges),
                    this,
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundDispatcher.ForegroundScheduler).ConfigureAwait(false);

                OnCompletingBackgroundWork();

                lock (_work)
                {
                    // Resetting the timer allows another batch of work to start.
                    _timer.Dispose();
                    _timer = null;

                    // If more work came in while we were running start the worker again.
                    if (_work.Count > 0)
                    {
                        StartWorker();
                    }
                }

                OnCompletedBackgroundWork();
            }
            catch (Exception ex)
            {
                // This is something totally unexpected, let's just send it over to the workspace.
                await Task.Factory.StartNew(
                    (p) => ((ProjectSnapshotManagerBase)p).ReportError(ex),
                    _projectManager,
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundDispatcher.ForegroundScheduler).ConfigureAwait(false);
            }
        }

        private void ReportWorkspaceStateChanges(IReadOnlyDictionary<string, ProjectWorkspaceState> workspaceStateChanges)
        {
            _foregroundDispatcher.AssertForegroundThread();

            foreach (var workspaceStateChange in workspaceStateChanges)
            {
                _projectManager.ProjectWorkspaceStateChanged(workspaceStateChange.Key, workspaceStateChange.Value);
            }
        }

        private void ReportError(ProjectSnapshot project, Exception ex)
        {
            GC.KeepAlive(Task.Factory.StartNew(
                (p) => ((ProjectSnapshotManagerBase)p).ReportError(ex, project),
                _projectManager,
                CancellationToken.None,
                TaskCreationOptions.None,
                _foregroundDispatcher.ForegroundScheduler));
        }

        private sealed class TagHelperResolutionRequest
        {
            public TagHelperResolutionRequest(Project workspaceProject, ProjectSnapshot projectSnapshot)
            {
                if (projectSnapshot == null)
                {
                    throw new ArgumentNullException(nameof(projectSnapshot));
                }

                WorkspaceProject = workspaceProject;
                ProjectSnapshot = projectSnapshot;
            }

            public Project WorkspaceProject { get; }

            public ProjectSnapshot ProjectSnapshot { get; }
        }
    }
}