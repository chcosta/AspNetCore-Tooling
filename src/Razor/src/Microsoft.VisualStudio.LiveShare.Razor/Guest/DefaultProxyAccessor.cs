// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.LiveShare.Razor.Guest
{
    [System.Composition.Shared]
    [Export(typeof(ProxyAccessor))]
    public class DefaultProxyAccessor : ProxyAccessor
    {
        private readonly LiveShareClientProvider _liveShareClientProvider;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private IProjectSnapshotManagerProxy _projectSnapshotManagerProxy;
        private IProjectHierarchyProxy _projectHierarchyProxy;

        [ImportingConstructor]
        public DefaultProxyAccessor(
            LiveShareClientProvider liveShareClientProvider,
            JoinableTaskContext joinableTaskContext)
        {
            if (liveShareClientProvider == null)
            {
                throw new ArgumentNullException(nameof(liveShareClientProvider));
            }

            if (joinableTaskContext == null)
            {
                throw new ArgumentNullException(nameof(joinableTaskContext));
            }

            _liveShareClientProvider = liveShareClientProvider;
            _joinableTaskFactory = joinableTaskContext.Factory;
        }

        // Testing constructor
        private protected DefaultProxyAccessor()
        {
        }

        public override IProjectSnapshotManagerProxy GetProjectSnapshotManagerProxy()
        {
            if (_projectSnapshotManagerProxy == null)
            {
                _projectSnapshotManagerProxy = CreateServiceProxy<IProjectSnapshotManagerProxy>();
            }

            return _projectSnapshotManagerProxy;
        }

        public override IProjectHierarchyProxy GetProjectHierarchyProxy()
        {
            if (_projectHierarchyProxy == null)
            {
                _projectHierarchyProxy = CreateServiceProxy<IProjectHierarchyProxy>();
            }

            return _projectHierarchyProxy;
        }

        // Internal virtual for testing
        internal virtual TProxy CreateServiceProxy<TProxy>() where TProxy : class
        {
            return _joinableTaskFactory.Run(() => _liveShareClientProvider.CreateServiceProxyAsync<TProxy>());
        }
    }
}
