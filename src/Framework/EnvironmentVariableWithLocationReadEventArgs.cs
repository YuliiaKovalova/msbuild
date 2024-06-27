// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Build.Framework
{
    /// <summary>
    /// Arguments for the environment variable read event with the location where it happened.
    /// </summary>
    [Serializable]
    public class EnvironmentVariableWithLocationReadEventArgs : EnvironmentVariableReadEventArgs
    {
        /// <summary>
        /// Initializes an instance of the EnvironmentVariableReadEventArgs class.
        /// </summary>
        public EnvironmentVariableWithLocationReadEventArgs()
        {
        }

        public EnvironmentVariableWithLocationReadEventArgs(
            string environmentVariableName,
            string message,
            string fileName,
            int line,
            int column,
            string? helpKeyword = null,
            string? senderName = null,
            MessageImportance importance = MessageImportance.Low)
            : base(environmentVariableName, message, helpKeyword, senderName, importance)
        {
        }

        /// <summary>
        /// The line number where this element exists in its file.
        /// The first line is numbered 1.
        /// Zero indicates "unknown location".
        /// </summary>
        public int Line { get; set; }

        /// <summary>
        /// The line number where this element exists in its file.
        /// The first line is numbered 1.
        /// Zero indicates "unknown location".
        /// </summary>
        public int Column { get; set; }
    }
}
