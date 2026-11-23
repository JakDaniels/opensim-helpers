// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl;

/// <summary>An error with a message meant for the person running osctl (printed without a stack trace).</summary>
public class OsctlException(string message) : Exception(message);
