using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiyFfb.GraphEditor
{
    public static class GraphTemplateRegistry
    {
        private static readonly List<GraphTemplateEntry> AllTemplates = new List<GraphTemplateEntry>
        {
            new GraphTemplateEntry
            {
                Name = "Fixed-Wing Aircraft (Basic)",
                Description = "Basic FFB graph for fixed-wing aircraft with aerodynamic forces and trim.",
                Category = "Flight",
                TemplatePath = "graphs/templates/plane_default.json",
                GameIds = new[] { "X-Plane", "XPlane", "XPlane11", "XPlane12", "MSFS2020", "MSFS2024", "Prepar3D", "P3D", "FSX" }
            },
            new GraphTemplateEntry
            {
                Name = "Helicopter \u2014 Unboosted (X-Plane)",
                Description = "Unboosted helicopter (MD 500E, R22). Blade-alpha load force, OWL option, mechanical friction. Uses X-Plane ground-truth rotor signals.",
                Category = "Flight",
                TemplatePath = "graphs/templates/heli_unboosted.json",
                GameIds = new[] { "X-Plane", "XPlane", "XPlane11", "XPlane12" }
            },
            new GraphTemplateEntry
            {
                Name = "Helicopter \u2014 Unboosted (MSFS)",
                Description = "Unboosted helicopter for MSFS 2020/2024. Same feel model as the X-Plane variant; blade-alpha/VRS/slap/propwash derived in-plugin from IAS + descent rate + weight (plan 17 \u00a73). Per-aircraft tuning via Aircraft.* graph params.",
                Category = "Flight",
                TemplatePath = "graphs/templates/heli_unboosted_msfs.json",
                GameIds = new[] {
                    "MSFS2020", "MSFS2024",
                    "FlightSimulator", "FlightSimulator2020", "FlightSimulator2024",
                    "MicrosoftFlightSimulator", "MicrosoftFlightSimulator2020", "MicrosoftFlightSimulator2024",
                    "MSFS", "MSFS20", "MSFS24"
                }
            },
            new GraphTemplateEntry
            {
                Name = "Helicopter \u2014 FFB API (MSFS)",
                Description = "Helicopter for MSFS aircraft that implement the FFB LVAR API (Standards/MSFS_Helicopter_FFB_API.md). Same feel model as the Unboosted (MSFS) variant, but trim, trim-release and hydraulic assist come from the aircraft over L:FFB_* instead of being inferred; fly-through is reported back per axis. On an aircraft that does not implement the API every read is 0, which reads as \"no trim system, full boost\" \u2014 use the Unboosted (MSFS) template there instead, since this one still writes L:FFB_*_ENABLED.",
                Category = "Flight",
                TemplatePath = "graphs/templates/heli_ffbapi_msfs.json",
                GameIds = new[] {
                    "MSFS2020", "MSFS2024",
                    "FlightSimulator", "FlightSimulator2020", "FlightSimulator2024",
                    "MicrosoftFlightSimulator", "MicrosoftFlightSimulator2020", "MicrosoftFlightSimulator2024",
                    "MSFS", "MSFS20", "MSFS24"
                }
            },
            new GraphTemplateEntry
            {
                Name = "Helicopter \u2014 Boosted",
                Description = "Boosted helicopter (Bell 206, H125). No load force, hydraulic friction/damping model.",
                Category = "Flight",
                TemplatePath = "graphs/templates/heli_boosted.json",
                GameIds = new[] { "X-Plane", "XPlane", "XPlane11", "XPlane12", "MSFS2020", "MSFS2024" }
            },
            new GraphTemplateEntry
            {
                Name = "Helicopter \u2014 SAS",
                Description = "SAS-equipped helicopter (Bell 222). G-load + rate load force, hydraulic friction/damping.",
                Category = "Flight",
                TemplatePath = "graphs/templates/heli_sas.json",
                GameIds = new[] { "X-Plane", "XPlane", "XPlane11", "XPlane12", "MSFS2020", "MSFS2024" }
            },
            new GraphTemplateEntry
            {
                Name = "Automotive (Basic)",
                Description = "Placeholder graph for sim racing vehicles. No signal processing yet \u2014 pedal forces use hardware config only.",
                Category = "Automotive",
                TemplatePath = "graphs/templates/vehicle_default.json",
                GameIds = new[]
                {
                    "BeamNGdrive", "BeamNG",
                    "AssettoCorsa", "AssettoCorsaCompetizione",
                    "iRacing",
                    "rFactor2",
                    "RaceRoom",
                    "Automobilista2",
                    "LeMansUltimate",
                    "F12024", "F12023",
                    "ProjectCARS2", "ProjectCARS3",
                    "DirtRally2", "WRC",
                    "ForzaMotorsport"
                }
            }
        };

        /// <summary>
        /// Gets all available templates filtered by game ID.
        /// </summary>
        /// <param name="gameId">The game ID to filter by, or null to get all templates.</param>
        /// <param name="baseDirectory">Base directory for resolving template paths.</param>
        /// <returns>Enumerable of template entries that match the game ID.</returns>
        public static IEnumerable<GraphTemplateEntry> GetTemplates(string gameId, string baseDirectory)
        {
            // Log so the user can see what SimHub actually reports when a template
            // is missing for their game (game-ID strings aren't documented anywhere).
            SimHub.Logging.Current?.Info(
                $"[GraphTemplateRegistry] GetTemplates(gameId='{gameId ?? "(null)"}')");

            var filtered = string.IsNullOrWhiteSpace(gameId)
                ? AllTemplates
                : AllTemplates.Where(t => t.GameIds != null &&
                                         t.GameIds.Any(g => string.Equals(g, gameId, StringComparison.OrdinalIgnoreCase)));

            // Verify template files exist and return only valid ones
            var result = new List<GraphTemplateEntry>();
            foreach (var template in filtered)
            {
                string resolvedPath = ResolveTemplatePath(template.TemplatePath, baseDirectory);
                if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
                {
                    result.Add(template);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the full path to a template file.
        /// </summary>
        /// <param name="templatePath">The template path (relative or absolute).</param>
        /// <param name="baseDirectory">Base directory for resolving relative paths.</param>
        /// <returns>Full path to the template file, or null if resolution fails.</returns>
        public static string ResolveTemplatePath(string templatePath, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(templatePath))
            {
                return null;
            }

            if (Path.IsPathRooted(templatePath))
            {
                return templatePath;
            }

            // Try base directory first
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                string combined = Path.Combine(baseDirectory, templatePath);
                if (File.Exists(combined))
                {
                    return combined;
                }
            }

            // Try current directory
            if (File.Exists(templatePath))
            {
                return Path.GetFullPath(templatePath);
            }

            // Try plugin directory
            string pluginDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(pluginDir))
            {
                string combined = Path.Combine(pluginDir, templatePath);
                if (File.Exists(combined))
                {
                    return combined;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if any templates are available for the given game.
        /// </summary>
        /// <param name="gameId">The game ID to check.</param>
        /// <param name="baseDirectory">Base directory for resolving template paths.</param>
        /// <returns>True if at least one template is available.</returns>
        public static bool HasTemplatesForGame(string gameId, string baseDirectory)
        {
            return GetTemplates(gameId, baseDirectory).Any();
        }
    }
}
