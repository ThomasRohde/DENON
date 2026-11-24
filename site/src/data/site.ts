import versions from "../generated/versions.json";

const repo = "https://github.com/ThomasRohde/DENON";

/** A path on this site, with the GitHub Pages base (/DENON). */
export const url = (path = "") => `${import.meta.env.BASE_URL.replace(/\/$/, "")}/${path.replace(/^\//, "")}`;

export const links = {
  repo,
  releases: `${repo}/releases`,
  latest: `${repo}/releases/latest`,
  installer: `${repo}/releases/latest/download/DenonDesktop-Setup-win-x64.exe`,
  portable: `${repo}/releases/latest/download/Denon.App.Desktop-win-x64.exe`,
  checksums: `${repo}/releases/latest/download/SHA256SUMS.txt`,
  changelog: `${repo}/blob/master/CHANGELOG.md`,
  license: `${repo}/blob/master/LICENSE`,
  issues: `${repo}/issues`,
  chromecast: `${repo}/releases/download/chromecast-v${versions.chromecast}/Denon.Plugin.Chromecast-${versions.chromecast}.zip`,
  appleTv: `${repo}/releases/download/appletv-v${versions.appleTv}/Denon.Plugin.AppleTv-${versions.appleTv}.zip`,
  homePod: `${repo}/releases/download/homepod-v${versions.homePod}/Denon.Plugin.HomePod-${versions.homePod}.zip`,
};

export { versions };

/** The app's pages, in the order of its left rail, with the icon it uses and a screenshot. */
export const tour = [
  {
    id: "deck",
    label: "Deck",
    icon: "IconNavControlDeck",
    shot: "01-control-deck",
    guide: "control-deck",
    title: "The control deck",
    text: "Volume, inputs, sound modes and sleep timer in one place, next to what plays now. The VU meters and the 31-band spectrum follow the real audio the app streams to the receiver.",
  },
  {
    id: "music",
    label: "Music",
    icon: "IconNavYouTube",
    shot: "02-youtube-music",
    guide: "youtube-cinema",
    title: "YouTube and YouTube Music",
    text: "Search or paste a link and the receiver plays it. Build a queue, and watch the video in a Cinema window that stays in sync with the speakers.",
  },
  {
    id: "stations",
    label: "Stations",
    icon: "IconRadio",
    shot: "05-global-radio-explorer",
    guide: "stations-radio",
    title: "Internet radio",
    text: "Search more than 40,000 stations by name, genre, country or bit rate. Save the ones you like as presets on the deck.",
  },
  {
    id: "cast",
    label: "Cast",
    icon: "IconNavCast",
    shot: "04-cast-phone-remote",
    guide: "cast-phone-remote",
    title: "Cast and the phone remote",
    text: "Send your PC's sound to the receiver, play a folder of FLAC files, or scan a QR code to use your phone as a remote. The phone needs no app.",
  },
  {
    id: "scenes",
    label: "Scenes",
    icon: "IconNavScenes",
    shot: "03-scenes-acoustics",
    guide: "scenes-acoustics",
    title: "Scenes and acoustics",
    text: "One click sets input, sound mode, volume and lights for a movie night. Tune Audyssey, Dynamic EQ, dialog level and tone live.",
  },
  {
    id: "plugins",
    label: "Plugins",
    icon: "IconNavPlugins",
    shot: "12-plugin-philips-hue",
    guide: "plugin-hue",
    title: "Lights, TV and the smart home",
    text: "Philips Hue, LG webOS TVs and Home Assistant are built in. Chromecast, Apple TV and HomePod come as plugins you can add.",
  },
  {
    id: "brain",
    label: "Brain",
    icon: "IconNavMcp",
    shot: "08-brain",
    guide: "mcp-room-brain",
    title: "AI assistants (MCP)",
    text: "An optional MCP server lets an AI assistant such as Claude see the room and control it, with the access level you choose.",
  },
  {
    id: "settings",
    label: "Settings",
    icon: "IconNavSettings",
    shot: "06-settings-shortcuts",
    guide: "shortcuts-hotkeys",
    title: "Settings and shortcuts",
    text: "Find the receiver on the network, choose the look, and set keyboard shortcuts that work in any app. The editor warns you when two shortcuts collide.",
  },
] as const;

/** The screenshot shown at the top of each guide topic (none for the plugins that are separate downloads). */
export const topicShots: Record<string, string> = {
  "quick-start": "00-splash",
  "window-layout": "01-control-deck",
  "control-deck": "01-control-deck",
  "youtube-cinema": "02-youtube-music",
  "scenes-acoustics": "03-scenes-acoustics",
  "cast-phone-remote": "04-cast-phone-remote",
  "stations-radio": "05-global-radio-explorer",
  "plugin-hue": "12-plugin-philips-hue",
  "plugin-smart-tv": "13-plugin-smart-tv",
  "plugin-home-assistant": "14-plugin-home-assistant",
  "mcp-room-brain": "08-brain",
  "shortcuts-hotkeys": "06-settings-shortcuts",
  "ports-troubleshooting": "09-health",
};

/** URL-safe id for a Help category. */
export const slug = (text: string) => text.toLowerCase().replace(/&/g, "and").replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
