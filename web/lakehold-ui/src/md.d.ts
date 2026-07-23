/**
 * Raw-text import for Markdown files. The build treats `.md` as a text loader
 * (see `angular.json` → `build.options.loader`), so importing one yields its contents as a string.
 */
declare module '*.md' {
  const content: string;
  export default content;
}
