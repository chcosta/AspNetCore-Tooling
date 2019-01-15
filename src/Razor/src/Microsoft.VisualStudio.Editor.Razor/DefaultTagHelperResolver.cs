// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;

namespace Microsoft.VisualStudio.Editor.Razor
{
    internal class DefaultTagHelperResolver : TagHelperResolver
    {
        public override Task<TagHelperResolutionResult> GetTagHelpersAsync(Project project, ProjectSnapshot projectSnapshot, CancellationToken cancellationToken = default)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (projectSnapshot == null)
            {
                throw new ArgumentNullException(nameof(projectSnapshot));
            }

            if (projectSnapshot.Configuration == null)
            {
                return Task.FromResult(TagHelperResolutionResult.Empty);
            }
            
            return GetTagHelpersAsync(project, projectSnapshot.GetProjectEngine());
        }
    }
}
