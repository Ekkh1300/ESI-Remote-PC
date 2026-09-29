// Implicit usings shared by every file in the platform interop assembly. These are the
// only three dependencies the low-level interop layer has, which keeps it usable from
// both the session-0 service and the per-user session agent.

global using Esi.Protocol;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Options;
