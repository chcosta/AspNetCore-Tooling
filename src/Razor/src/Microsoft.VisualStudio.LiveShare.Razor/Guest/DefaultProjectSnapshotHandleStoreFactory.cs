// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.LiveShare.Razor.Guest
{
    [ExportLanguageServiceFactory(typeof(ProjectSnapshotHandleStore), RazorLanguage.Name, Constants.GuestOnlyWorkspaceLayer)]
    internal class DefaultProjectSnapshotHandleStoreFactory : ILanguageServiceFactory
    {
        private readonly ForegroundDispatcher _foregroundDispatcher;
        private readonly ProxyAccessor _proxyAccessor;
        private readonly JoinableTaskContext _joinableTaskContext;

        [ImportingConstructor]
        public DefaultProjectSnapshotHandleStoreFactory(
            ForegroundDispatcher foregroundDispatcher,
            ProxyAccessor proxyAccessor,
            JoinableTaskContext joinableTaskContext)
        {
            if (foregroundDispatcher == null)
            {
                throw new ArgumentNullException(nameof(foregroundDispatcher));
            }

            if (proxyAccessor == null)
            {
                throw new ArgumentNullException(nameof(proxyAccessor));
            }

            if (joinableTaskContext == null)
            {
                throw new ArgumentNullException(nameof(joinableTaskContext));
            }

            _foregroundDispatcher = foregroundDispatcher;
            _proxyAccessor = proxyAccessor;
            _joinableTaskContext = joinableTaskContext;
        }

        public ILanguageService CreateLanguageService(HostLanguageServices languageServices)
        {
            if (languageServices == null)
            {
                throw new ArgumentNullException(nameof(languageServices));
            }

            var snapshotStore = new DefaultProjectSnapshotHandleStore(
                _foregroundDispatcher,
                _joinableTaskContext.Factory,
                _proxyAccessor);
            snapshotStore.InitializeProjects();

            return snapshotStore;
        }
    }
}
