import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../', import.meta.url))
const commands = {
  restore: [
    ['dotnet', 'restore', 'ErpGenerator.slnx'],
    ['npm', '--prefix', 'portal', 'ci'],
    ['npm', '--prefix', 'api/integrations/nango', 'ci'],
  ],
  build: [
    ['dotnet', 'build', 'ErpGenerator.slnx', '--no-restore'],
    ['npm', '--prefix', 'api/integrations/nango', 'run', 'typecheck'],
    ['npm', '--prefix', 'portal', 'run', 'build'],
  ],
  test: [
    ['dotnet', 'test', 'ErpGenerator.slnx', '--no-build'],
    ['npm', '--prefix', 'api/integrations/nango', 'test'],
    ['npm', '--prefix', 'portal', 'test'],
  ],
}
const action = process.argv[2]
if (!commands[action]) throw new Error('Expected restore, build, or test.')
for (const [command, ...args] of commands[action]) {
  const result = spawnSync(command, args, { cwd: root, stdio: 'inherit', env: process.env })
  if (result.error) throw result.error
  if (result.status !== 0) process.exit(result.status ?? 1)
}
