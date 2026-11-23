// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Cli;
using Osctl.Util;

// Group-shared files: what osctl creates, any member of the opensim group may change (PLAN.md "Packaging").
Posix.SetUmask(0b000_000_111);
return await CliApp.RunAsync(args, CliEnvironment.Console());
