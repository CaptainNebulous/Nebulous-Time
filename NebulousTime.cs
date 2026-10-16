using Oxide.Core;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using System;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Nebulous Time", "CaptainNebulous", "1.9.14")]
    [Description("Displays game time and the current and next dynamic weather states when accessible.")]
    public class NebulousTime : RustPlugin
    {
        private const string Red = "#D65B5B";
        private const string Grey = "#ABB3BE";
        private const string White = "#ECEFF3";
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private string current, next;
        private string source = "Unavailable";
        private Timer poll;
        private object cachedClimate;
        private Type cachedClimateType;
        private MemberInfo currentWeatherMember;
        private MemberInfo nextWeatherMember;
        private string lastWarning;

        private void Init() => cmd.AddChatCommand("time", this, nameof(TimeCommand));

        private void OnServerInitialized()
        {
            RefreshWeather();
            poll = timer.Every(20f, RefreshWeather);
        }

        private void Unload() => poll?.Destroy();

        private void RefreshWeather()
        {
            try
            {
                object climate = cachedClimate;
                if (climate is UnityEngine.Object && (UnityEngine.Object)climate == null)
                    climate = cachedClimate = null;
                if (climate == null) climate = cachedClimate = FindClimate();
                if (climate == null)
                {
                    current = next = null;
                    source = "Climate component not found";
                    return;
                }

                if (cachedClimateType != climate.GetType())
                {
                    cachedClimateType = climate.GetType();
                    currentWeatherMember = FindMember(cachedClimateType, "WeatherStateTarget");
                    nextWeatherMember = FindMember(cachedClimateType, "WeatherStateNext");
                }
                object active = ReadCachedMember(climate, currentWeatherMember);
                object upcoming = ReadCachedMember(climate, nextWeatherMember);

                // The active target is the 'current' entry in weather.report;
                // WeatherState itself can be a blended runtime preset.
                string activeName = PresetName(active);
                string nextName = PresetName(upcoming);

                if (string.IsNullOrEmpty(activeName) || string.IsNullOrEmpty(nextName))
                {
                    current = next = null;
                    source = "Weather presets unavailable";
                    return;
                }

                current = Pretty(activeName);
                next = Pretty(nextName);
                source = "Climate";
            }
            catch (Exception ex)
            {
                current = next = null;
                source = "Climate read failed";
                string warning = ex.GetBaseException().Message;
                if (warning != lastWarning)
                {
                    PrintWarning("Unable to read Rust climate weather presets: " + warning);
                    lastWarning = warning;
                }
                cachedClimate = null;
            }
        }

        private object FindClimate()
        {
            Type climateType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                climateType = assembly.GetType("Climate", false);
                if (climateType != null) break;
            }
            if (climateType == null) return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var generic = assembly.GetType("SingletonComponent`1", false);
                if (generic == null) continue;
                try
                {
                    var singleton = generic.MakeGenericType(climateType);
                    var instance = ReadMember(singleton, null, "Instance");
                    if (instance != null) return instance;
                }
                catch { }
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(climateType))
                return UnityEngine.Object.FindObjectOfType(climateType);
            return null;
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            return (MemberInfo)type.GetProperty(name, Flags) ?? type.GetField(name, Flags);
        }

        private static object ReadCachedMember(object target, MemberInfo member)
        {
            var property = member as PropertyInfo;
            if (property != null) return property.GetValue(target, null);
            var field = member as FieldInfo;
            return field == null ? null : field.GetValue(target);
        }

        private object ReadMember(object target, string memberName)
        {
            return ReadMember(target.GetType(), target, memberName);
        }

        private object ReadMember(Type type, object target, string memberName)
        {
            var property = type.GetProperty(memberName, Flags);
            if (property != null) return property.GetValue(target, null);
            var field = type.GetField(memberName, Flags);
            if (field != null) return field.GetValue(target);
            return null;
        }

        private string PresetName(object preset)
        {
            if (preset == null) return null;
            var unityObject = preset as UnityEngine.Object;
            if (unityObject != null) return unityObject.name;
            object name = ReadMember(preset, "name") ?? ReadMember(preset, "Name");
            return name == null ? null : name.ToString();
        }

        private string Pretty(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "rainheavy": return "Heavy rain";
                case "rainmild": return "Light rain";
                case "storm": return "Storm";
                case "fog": return "Fog";
                case "dust": return "Dust";
                case "overcast": return "Overcast";
                case "clear": return "Clear";
                default: return value;
            }
        }

        private void TimeCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var sky = TOD_Sky.Instance;
            if (sky == null || sky.Cycle == null)
            {
                SendReply(player, "<color=" + Red + ">TIME & WEATHER</color> | The game clock is not ready.");
                return;
            }
            int totalMinutes = Mathf.FloorToInt(Mathf.Repeat(sky.Cycle.Hour, 24f) * 60f);
            int hour = totalMinutes / 60;
            int minute = totalMinutes % 60;
            int hour12 = hour % 12 == 0 ? 12 : hour % 12;
            string time = hour12.ToString(CultureInfo.InvariantCulture) + ":" + minute.ToString("00") + (hour < 12 ? " AM" : " PM");

            var sb = new StringBuilder();
            sb.Append("<color=").Append(Red).Append("><b>TIME & WEATHER</b></color>\n");
            Line(sb, "In-game time", time);
            Line(sb, "Current Weather", current ?? "Unavailable");
            Line(sb, "Upcoming Weather", next ?? "Unavailable");
            if (source != "Climate")
                sb.Append("<color=").Append(Grey).Append("><size=11>Weather information unavailable: " + source + ".</size></color>");
            SendReply(player, sb.ToString());
        }

        private void Line(StringBuilder sb, string label, string value)
        {
            sb.Append("<color=").Append(Grey).Append(">").Append(label).Append(":</color> <color=").Append(White).Append(">")
              .Append(value).Append("</color>\n");
        }
    }
}