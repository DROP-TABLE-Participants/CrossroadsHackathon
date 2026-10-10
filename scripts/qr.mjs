import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync } from 'node:fs'
const configured = existsSync('.env.demo') ? readFileSync('.env.demo','utf8').split('\n').find(v => v.startsWith('FRONTEND_URL='))?.slice('FRONTEND_URL='.length) : undefined
const value = process.argv[2] || configured
if (!value) throw new Error('Provide the Portal entry URL: npm run demo:qr -- https://your-host/en/account')
const url = new URL(value)
if (!['http:','https:'].includes(url.protocol)) throw new Error('Use an HTTP or HTTPS entry URL.')
mkdirSync('output',{recursive:true})
// One pinned CLI for the explicitly requested QR artifact; no application dependency.
const result = spawnSync('npx',['--yes','--package=qrcode@1.5.4','qrcode','--type','svg','--output','output/demo-qr.svg',url.href],{stdio:'inherit'})
if (result.status !== 0) process.exit(result.status ?? 1)
console.log('QR saved: output/demo-qr.svg\nEntry URL: '+url.href)
