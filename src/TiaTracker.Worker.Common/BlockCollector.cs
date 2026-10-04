using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaTracker.Contracts;

namespace TiaTracker.Worker
{
    /// <summary>Un blocco o un UDT con il percorso completo del gruppo ("" = radice).</summary>
    internal sealed class CollectedItem
    {
        internal CollectedItem(IEngineeringObject obj, string family, string kind, string name, string groupPath)
        {
            Object = obj;
            Family = family;
            Kind = kind;
            Name = name;
            GroupPath = groupPath;
        }

        internal IEngineeringObject Object { get; private set; }

        internal string Family { get; private set; }

        internal string Kind { get; private set; }

        internal string Name { get; private set; }

        internal string GroupPath { get; private set; }

        internal PlcBlock Block
        {
            get { return Object as PlcBlock; }
        }

        internal PlcType Type
        {
            get { return Object as PlcType; }
        }
    }

    internal static class BlockCollector
    {
        internal static List<CollectedItem> Collect(PlcSoftware plc)
        {
            List<CollectedItem> result = new List<CollectedItem>();
            PlcBlockSystemGroup blocks = plc.BlockGroup;
            AddBlocks(blocks.Blocks, string.Empty, result);
            foreach (PlcBlockUserGroup g in blocks.Groups)
            {
                WalkBlocks(g, g.Name, result);
            }

            PlcTypeSystemGroup types = plc.TypeGroup;
            AddTypes(types.Types, string.Empty, result);
            foreach (PlcTypeUserGroup g in types.Groups)
            {
                WalkTypes(g, g.Name, result);
            }

            return result;
        }

        internal static int CountBlocks(PlcSoftware plc)
        {
            int n = 0;
            foreach (PlcBlock b in plc.BlockGroup.Blocks)
            {
                n++;
            }

            foreach (PlcBlockUserGroup g in plc.BlockGroup.Groups)
            {
                n += CountBlocks(g);
            }

            return n;
        }

        private static int CountBlocks(PlcBlockUserGroup group)
        {
            int n = 0;
            foreach (PlcBlock b in group.Blocks)
            {
                n++;
            }

            foreach (PlcBlockUserGroup g in group.Groups)
            {
                n += CountBlocks(g);
            }

            return n;
        }

        internal static int CountTypes(PlcSoftware plc)
        {
            int n = 0;
            foreach (PlcType t in plc.TypeGroup.Types)
            {
                n++;
            }

            foreach (PlcTypeUserGroup g in plc.TypeGroup.Groups)
            {
                n += CountTypes(g);
            }

            return n;
        }

        private static int CountTypes(PlcTypeUserGroup group)
        {
            int n = 0;
            foreach (PlcType t in group.Types)
            {
                n++;
            }

            foreach (PlcTypeUserGroup g in group.Groups)
            {
                n += CountTypes(g);
            }

            return n;
        }

        private static void WalkBlocks(PlcBlockUserGroup group, string path, List<CollectedItem> result)
        {
            AddBlocks(group.Blocks, path, result);
            foreach (PlcBlockUserGroup child in group.Groups)
            {
                WalkBlocks(child, path + "/" + child.Name, result);
            }
        }

        private static void AddBlocks(PlcBlockComposition blocks, string path, List<CollectedItem> result)
        {
            foreach (PlcBlock b in blocks)
            {
                result.Add(new CollectedItem(b, Families.Block, KindOf(b), b.Name, path));
            }
        }

        private static void WalkTypes(PlcTypeUserGroup group, string path, List<CollectedItem> result)
        {
            AddTypes(group.Types, path, result);
            foreach (PlcTypeUserGroup child in group.Groups)
            {
                WalkTypes(child, path + "/" + child.Name, result);
            }
        }

        private static void AddTypes(PlcTypeComposition types, string path, List<CollectedItem> result)
        {
            foreach (PlcType t in types)
            {
                result.Add(new CollectedItem(t, Families.Type, "UDT", t.Name, path));
            }
        }

        /// <summary>
        /// InstanceDB copre sia le istanze di FB sia i DB tipizzati su UDT:
        /// la distinzione sta in InstanceOfType.
        /// </summary>
        internal static string KindOf(PlcBlock block)
        {
            if (block is OB)
            {
                return "OB";
            }

            if (block is FB)
            {
                return "FB";
            }

            if (block is FC)
            {
                return "FC";
            }

            if (block is InstanceDB)
            {
                return "InstanceDB";
            }

            if (block is ArrayDB)
            {
                return "ArrayDB";
            }

            if (block is GlobalDB)
            {
                return "GlobalDB";
            }

            return block.GetType().Name;
        }

        /// <summary>
        /// Come Exporter.CanGenerateSource del toolkit: GenerateSource copre
        /// FB/FC/OB in SCL o STL, i DB globali e gli UDT.
        /// </summary>
        internal static bool CanGenerateSource(CollectedItem item, string language, out string reason)
        {
            reason = null;
            if (item.Type != null)
            {
                return true;
            }

            PlcBlock block = item.Block;
            if (block is InstanceDB)
            {
                reason = "DB di istanza";
                return false;
            }

            if (block is ArrayDB)
            {
                reason = "array DB";
                return false;
            }

            if (block is GlobalDB)
            {
                return true;
            }

            if (language == "SCL" || language == "STL")
            {
                return true;
            }

            reason = "linguaggio " + (language ?? "?");
            return false;
        }
    }
}
