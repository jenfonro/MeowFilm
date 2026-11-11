import { readFileSync } from 'node:fs';

export const novelRuleSource = readFileSync(new URL('./novel-rules.js', import.meta.url), 'utf8');
