// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.LiveShare.Razor.Guest
{
    [System.Composition.Shared]
    [Export]
    [ExportCollaborationService(
        typeof(DisposeDetector),
        Name = nameof(DisposeDetector),
        Scope = SessionScope.Guest,
        Role = ServiceRole.RemoteClient)]
    internal class RazorGuestRemoteSessionDetector : ICollaborationServiceFactory
    {
        public bool IsRemoteSession { get; private set; }

        public Task<ICollaborationService> CreateServiceAsync(CollaborationSession sessionContext, CancellationToken cancellationToken)
        {
            if (sessionContext == null)
            {
                throw new ArgumentNullException(nameof(sessionContext));
            }

            IsRemoteSession = true;

            ICollaborationService disposeDetector = new DisposeDetector(() => IsRemoteSession = false);
            return Task.FromResult(disposeDetector);
        }
    }

    public class DisposeDetector : ICollaborationService, IDisposable
    {
        private readonly Action _onDispose;

        public DisposeDetector(Action onDispose)
        {
            if (onDispose == null)
            {
                throw new ArgumentNullException(nameof(onDispose));
            }

            _onDispose = onDispose;
        }

        public void Dispose()
        {
            _onDispose();
        }
    }
}
