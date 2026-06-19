// VEX ADDITION (additive): lets the test assembly exercise the internal seams
// (VexAgents registry, VexChatHistory transcript, FlueJobs) directly.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Vex.Assistant.Tests")]
