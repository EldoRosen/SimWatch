using System;
using System.Globalization;
using Sandbox.Game.Entities;

namespace SimWatch
{
    /// <summary>Finds a grid by the name the Profiler printed and returns a GPS string for its position.</summary>
    public static class GridLocator
    {
        static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            name = name.Trim();
            if (name.EndsWith("...", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 3);
            return string.Join(" ", name.ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        static string SafeName(string name)
        {
            // ':' separates the GPS fields, and the string is shown inside a Discord code span
            return (name ?? "grid").Replace(':', ' ').Replace('`', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>
        /// "GPS:name:x:y:z:#FF0000:" for the grid whose name matches, null if none does. A grid with exactly that name wins over
        /// one that merely contains it; among equals the grid with the most blocks is taken. Call on the game thread.
        /// </summary>
        public static string FindGps(string displayName)
        {
            var wanted = Normalize(displayName);
            if (wanted.Length == 0) return null;

            MyCubeGrid best = null;
            var bestScore = 0;
            var bestBlocks = -1;

            foreach (var entity in MyEntities.GetEntities())
            {
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.Closed || grid.MarkedForClose) continue;

                var name = Normalize(grid.DisplayName);
                if (name.Length == 0) continue;

                var score = 0;
                if (name == wanted) score = 2;
                else if (wanted.Length >= 4 && name.Length >= 4 && (name.Contains(wanted) || wanted.Contains(name))) score = 1;
                if (score == 0) continue;

                var blocks = grid.BlocksCount;
                if (score > bestScore || (score == bestScore && blocks > bestBlocks))
                {
                    best = grid;
                    bestScore = score;
                    bestBlocks = blocks;
                }
            }

            if (best == null) return null;

            var p = best.PositionComp.GetPosition();
            return string.Format(CultureInfo.InvariantCulture, "GPS:{0}:{1:0.00}:{2:0.00}:{3:0.00}:#FF0000:", SafeName(best.DisplayName), p.X, p.Y, p.Z);
        }
    }
}
