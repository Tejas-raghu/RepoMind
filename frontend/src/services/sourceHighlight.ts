import { createHighlighterCore } from 'shiki/core'
import { createJavaScriptRegexEngine } from 'shiki/engine/javascript'

export interface HighlightToken { content: string; color?: string }

const languageByExtension: Record<string, string> = {
  ts: 'typescript', tsx: 'typescript', js: 'javascript', jsx: 'javascript',
  vue: 'vue', cs: 'csharp', json: 'json', md: 'markdown',
}

const loaders: Record<string, () => Promise<unknown>> = {
  typescript: () => import('@shikijs/langs/typescript'),
  javascript: () => import('@shikijs/langs/javascript'),
  vue: () => import('@shikijs/langs/vue'),
  csharp: () => import('@shikijs/langs/csharp'),
  json: () => import('@shikijs/langs/json'),
  markdown: () => import('@shikijs/langs/markdown'),
}

const highlighter = createHighlighterCore({
  themes: [import('@shikijs/themes/github-dark')],
  langs: [],
  engine: createJavaScriptRegexEngine(),
})
const loaded = new Set<string>()

export function languageForPath(path: string): string | null {
  return languageByExtension[path.split('.').at(-1)?.toLowerCase() ?? ''] ?? null
}

export async function highlightSource(path: string, lines: string[]): Promise<HighlightToken[][] | null> {
  const language = languageForPath(path)
  if (!language || lines.length > 1_000) return null
  const instance = await highlighter
  if (!loaded.has(language)) {
    await instance.loadLanguage(await loaders[language]() as Parameters<typeof instance.loadLanguage>[0])
    loaded.add(language)
  }
  const result = instance.codeToTokens(lines.join('\n'), { lang: language, theme: 'github-dark' })
  return result.tokens.map((line) => line.map((token) => ({ content: token.content, color: token.color })))
}
