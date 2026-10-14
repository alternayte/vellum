// @ts-check
import { defineConfig } from 'astro/config'
import starlight from '@astrojs/starlight'
import starlightLinksValidator from 'starlight-links-validator'
import starlightLlmsTxt from 'starlight-llms-txt'

// The public address of the docs site. The sitemap and llms.txt need an absolute address.
// Set VELLUM_DOCS_SITE in the build that publishes the site.
const site = process.env.VELLUM_DOCS_SITE ?? 'http://localhost:4321'

export default defineConfig({
  site,
  integrations: [
    starlight({
      title: 'Vellum',
      description:
        'Vellum is an architecture workbench that you host yourself: model a system, trace a flow, state promises and export a specification for a coding agent.',
      favicon: '/favicon.svg',
      customCss: ['./src/styles/vellum.css'],
      // Dark is the default theme, as in the app. Starlight's own components follow the system.
      components: {
        ThemeProvider: './src/components/ThemeProvider.astro',
        ThemeSelect: './src/components/ThemeSelect.astro',
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/alternayte/vellum' }],
      editLink: { baseUrl: 'https://github.com/alternayte/vellum/edit/main/site/' },
      lastUpdated: false,
      pagination: true,
      plugins: [
        // The build fails when an internal link or a heading anchor does not resolve.
        starlightLinksValidator({ errorOnRelativeLinks: true, errorOnInvalidHashes: true }),
        starlightLlmsTxt({
          projectName: 'Vellum documentation',
          description:
            'Documentation for people who run and use Vellum. An agent that calls Vellum reads /llms.txt of the Vellum instance itself for the agent interface.',
        }),
      ],
      sidebar: [
        {
          label: 'Tutorials',
          items: [
            'tutorials/self-host-quickstart',
            'tutorials/first-model-and-trace',
            'tutorials/import-and-review',
            'tutorials/promise-loop',
          ],
        },
        {
          label: 'How-to',
          items: [
            {
              label: 'Model and trace',
              items: [
                'how-to/model-a-system',
                'how-to/trace-a-scenario',
                'how-to/compare-variants',
                'how-to/work-in-a-draft',
                'how-to/write-project-docs',
              ],
            },
            {
              label: 'Import from a repository',
              items: ['how-to/connect-github', 'how-to/import-a-repository', 'how-to/check-change-impact', 'how-to/report-impact-in-pull-request-ci'],
            },
            {
              label: 'Use the assistant',
              items: ['how-to/connect-an-ai-provider', 'how-to/design-with-the-assistant'],
            },
            {
              label: 'Promise and review',
              items: ['how-to/declare-a-promise', 'how-to/review-a-design', 'how-to/record-a-decision'],
            },
            {
              label: 'Hand off to an agent',
              items: ['how-to/export-for-an-agent', 'how-to/connect-a-coding-agent', 'how-to/use-the-http-api'],
            },
            {
              label: 'Cost and load',
              items: ['how-to/estimate-costs', 'how-to/stress-a-flow', 'how-to/load-test-with-k6'],
            },
            {
              label: 'Operate',
              items: [
                'how-to/configure-settings',
                'how-to/manage-members',
                'how-to/run-in-hosted-mode',
                'how-to/back-up-and-restore',
                'how-to/upgrade',
                'how-to/rotate-the-master-key',
              ],
            },
          ],
        },
        {
          label: 'Concepts',
          items: [
            'concepts/model-is-the-source-of-truth',
            'concepts/evidence-and-truth-labels',
            'concepts/sources-and-snapshots',
            'concepts/promises-and-evidence',
            'concepts/traces-are-declared',
            'concepts/findings-are-not-proof',
            'concepts/estimates-are-not-measurements',
            'concepts/what-an-agent-can-and-cannot-do',
            'concepts/what-leaves-your-instance',
            'concepts/local-and-hosted-profiles',
            'concepts/security-model',
          ],
        },
        {
          label: 'Reference',
          items: [
            // Written by scripts/gen-api-reference.mjs from the OpenAPI document on each build.
            { label: 'HTTP API', collapsed: true, items: [{ autogenerate: { directory: 'reference/http-api' } }] },
            'reference/mcp-tools',
            'reference/settings',
            'reference/bootstrap-environment',
            'reference/model-document-format',
            'reference/scenario-format',
            'reference/bundle-and-sdd-format',
            'reference/promises-and-evidence',
            'reference/review-rules',
            'reference/extraction-rules',
            'reference/cost-model',
            'reference/stress-model',
            'reference/k6-results-and-targets',
            'reference/ai-usage-and-prices',
            'reference/glossary',
          ],
        },
        {
          label: 'Troubleshooting',
          items: [
            'troubleshooting/start-up-refusals',
            'troubleshooting/setup-and-sign-in',
            'troubleshooting/providers-and-github',
            'troubleshooting/stale-results-and-build-ready',
            'troubleshooting/conflicts',
          ],
        },
        {
          label: 'Runbooks',
          items: [
            'runbooks/backup-and-restore',
            'runbooks/upgrade',
            { label: 'Rotate the master key', link: '/how-to/rotate-the-master-key/' },
            'runbooks/lost-master-key',
            'runbooks/diagnostics-and-logs',
          ],
        },
      ],
    }),
  ],
})
