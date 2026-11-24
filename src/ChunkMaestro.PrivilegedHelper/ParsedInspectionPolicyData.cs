using System.Collections.ObjectModel;

namespace ChunkMaestro.PrivilegedHelper;

// This closed result carries DATA, never measured facts or authority.
internal abstract partial class PolicyDecodeResult
{
    private PolicyDecodeResult()
    {
    }

    internal sealed class PolicyParseRefusal : PolicyDecodeResult
    {
        private PolicyParseRefusal(PolicyParseCode code)
        {
            Code = code;
        }

        internal PolicyParseCode Code
        {
            get;
        }

        internal static PolicyParseRefusal Create(PolicyParseCode code)
        {
            return new PolicyParseRefusal(code);
        }
    }

    internal sealed partial class ParsedInspectionPolicyData : PolicyDecodeResult
    {
        internal ulong Schema
        {
            get;
        }
        internal string MachineId
        {
            get;
        }
        internal string DeploymentId
        {
            get;
        }
        internal string InstanceId
        {
            get;
        }
        internal uint AgentUid
        {
            get;
        }
        internal uint InspectorUid
        {
            get;
        }
        internal uint GameUid
        {
            get;
        }
        internal uint AgentGid
        {
            get;
        }
        internal uint InspectorGid
        {
            get;
        }
        internal uint GameGid
        {
            get;
        }
        internal string UnitName
        {
            get;
        }
        internal string GameRootPath
        {
            get;
        }
        internal uint OwnerUid
        {
            get;
        }
        internal uint OwnerGid
        {
            get;
        }
        internal uint DeviceMajor
        {
            get;
        }
        internal uint DeviceMinor
        {
            get;
        }
        internal ulong Inode
        {
            get;
        }
        internal uint Mode
        {
            get;
        }
        internal string FragmentPath
        {
            get;
        }
        internal string FragmentSha256
        {
            get;
        }
        internal string ManifestPath
        {
            get;
        }
        internal string ManifestSha256
        {
            get;
        }
        internal string PolicyRevision
        {
            get;
        }
        internal ReadOnlyCollection<KeyValuePair<string, string>> DropIns
        {
            get;
        }

        private ParsedInspectionPolicyData(Node root, string revision)
        {
            Schema = (ulong) root.Fields["schema"].Number;
            MachineId = root.Fields["host"].Fields["machineId"].Text!;
            DeploymentId = root.Fields["host"].Fields["deploymentId"].Text!;
            InstanceId = root.Fields["instanceId"].Text!;
            AgentUid = (uint) root.Fields["identities"].Fields["agentUid"].Number;
            InspectorUid = (uint) root.Fields["identities"].Fields["inspectorUid"].Number;
            GameUid = (uint) root.Fields["identities"].Fields["gameUid"].Number;
            AgentGid = (uint) root.Fields["identities"].Fields["agentGid"].Number;
            InspectorGid = (uint) root.Fields["identities"].Fields["inspectorGid"].Number;
            GameGid = (uint) root.Fields["identities"].Fields["gameGid"].Number;
            UnitName = root.Fields["target"].Fields["unitName"].Text!;
            GameRootPath = root.Fields["target"].Fields["gameRoot"].Fields["path"].Text!;
            OwnerUid = (uint) root.Fields["target"].Fields["gameRoot"].Fields["ownerUid"].Number;
            OwnerGid = (uint) root.Fields["target"].Fields["gameRoot"].Fields["ownerGid"].Number;
            DeviceMajor = (uint) root.Fields["target"].Fields["gameRoot"].Fields["deviceMajor"].Number;
            DeviceMinor = (uint) root.Fields["target"].Fields["gameRoot"].Fields["deviceMinor"].Number;
            Inode = (ulong) root.Fields["target"].Fields["gameRoot"].Fields["inode"].Number;
            Mode = (uint) root.Fields["target"].Fields["gameRoot"].Fields["mode"].Number;
            FragmentPath = root.Fields["target"].Fields["fragment"].Fields["path"].Text!;
            FragmentSha256 = root.Fields["target"].Fields["fragment"].Fields["sha256"].Text!;
            ManifestPath = root.Fields["packageManifest"].Fields["path"].Text!;
            ManifestSha256 = root.Fields["packageManifest"].Fields["sha256"].Text!;
            PolicyRevision = revision;
            List<KeyValuePair<string, string>> bindings = [];
            foreach (Node binding in root.Fields["target"].Fields["dropIns"].Items)
            {
                bindings.Add(new KeyValuePair<string, string>(binding.Fields["path"].Text!, binding.Fields["sha256"].Text!));
            }
            DropIns = new ReadOnlyCollection<KeyValuePair<string, string>>(bindings.ToArray());
        }
    }
}

internal static class InstalledPolicyCodec
{
    // Caller must keep the span stable and exclusively unmodified during the copy.
    internal static PolicyDecodeResult Decode(ReadOnlySpan<byte> input, CancellationToken cancellationToken = default)
    {
        return PolicyDecodeResult.ParsedInspectionPolicyData.Decode(input, cancellationToken);
    }
}
