// The site is served from GitHub Pages at https://thomasrohde.github.io/DENON/.
import { defineConfig } from "astro/config";

export default defineConfig({
  site: "https://thomasrohde.github.io",
  base: "/DENON",
  trailingSlash: "ignore",
  build: { format: "directory" },
});
