﻿// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace Example.Extension
{
    using WixToolset.Data.WindowsInstaller;

    public static class ExampleTableDefinitions
    {
        public static readonly TableDefinition ExampleTable = new TableDefinition(
            "Example",
            new[]
            {
                new ColumnDefinition("Example", ColumnType.String, 72, true, false, ColumnCategory.Identifier),
                new ColumnDefinition("Value", ColumnType.String, 0, false, false, ColumnCategory.Formatted),
            }
            );

        public static readonly TableDefinition[] All = new[] { ExampleTable };
    }
}
