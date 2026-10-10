import { spawn, spawnSync } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { existsSync, mkdirSync, openSync, readFileSync, readlinkSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../', import.meta.url))
const stateDirectory = join(root, '.demo')
const envPath = join(root, '.env.demo')
mkdirSync(stateDirectory, { recursive: true })
const action = process.argv[2]
function run(command, args, env, capture = false) {
  const result = spawnSync(command, args, { cwd: root, env, encoding: 'utf8', stdio: capture ? 'pipe' : 'inherit' })
  if (result.status !== 0) throw new Error(capture ? (result.stderr || `${command} failed`) : `${command} failed`)
  return result.stdout?.trim()
}
function configure() {
  if (existsSync(envPath)) return
  const secret = () => randomBytes(32).toString('hex')
  const apiPassword = secret(), runtimePassword = secret(), internal = secret(), broker = secret(), portal = secret()
  const values = {
    DEMO_API_DATABASE_PASSWORD: apiPassword, DEMO_RUNTIME_DATABASE_PASSWORD: runtimePassword,
    DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_ENVIRONMENT: 'Development',
    ConnectionStrings__ControlPlane: `Host=127.0.0.1;Port=5441;Database=erpgenerator_demo_api;Username=postgres;Password=${apiPassword}`,
    Jwt__SigningKey: secret(),
    Portal__PublicBaseUrl: 'http://localhost:3001', PortalGateway__Secret: portal,
    InternalApi__Secret: internal, InternalApi__DatabaseSecretEncryptionKey: randomBytes(32).toString('base64'),
    Runtime__PublicHostSuffix: 'localhost:5039', Runtime__Scheme: 'http',
    RuntimeBroker__BaseUrl: 'http://127.0.0.1:5039', RuntimeBroker__Secret: broker,
    ControlPlane__BaseUrl: 'http://127.0.0.1:5214', ControlPlane__InternalSecret: internal,
    Runtime__InternalSecret: secret(), Runtime__SessionSecret: secret(), RuntimeDeploymentBroker__Secret: broker,
    RuntimeDeploymentBroker__LifecycleMachineAttestationSecret: secret(), RuntimeDeploymentBroker__LifecycleSupervisorAttestationSecret: secret(),
    RuntimeDatabase__AutoProvisionDatabases: 'true', RuntimeDatabase__CredentialMode: 'Template', RuntimeDatabase__ProvisionTenantAppUsers: 'false',
    RuntimeDatabase__AdminConnectionString: `Host=127.0.0.1;Port=5442;Database=postgres;Username=postgres;Password=${runtimePassword}`,
    RuntimeDatabase__TenantConnectionStringTemplate: `Host=127.0.0.1;Port=5442;Database={database};Username=postgres;Password=${runtimePassword}`,
    ERP_GENERATOR_API_INTERNAL_URL: 'http://127.0.0.1:5214', ERP_GENERATOR_PORTAL_INTERNAL_SECRET: portal,
    FRONTEND_URL: 'http://localhost:3001', NEXT_PUBLIC_SITE_URL: 'http://localhost:3001', ERP_GENERATOR_COOKIE_SECURE: 'false',
    Demo__PreparedCopies: '20', Nango__AllowedFileIntegrations: 'notion', Claude__ApiKey: '', Nango__SecretKey: '', Nango__WebhookSigningKey: '',
  }
  writeFileSync(envPath, Object.entries(values).map(([k,v]) => `${k}=${v}`).join('\n') + '\n', { mode: 0o600 })
  console.log('Created ignored .env.demo with local secrets. Existing files are preserved.')
}
configure()
if (process.argv.includes('--native')) {
  let text = readFileSync(envPath, 'utf8').replace(/^DEMO_DATABASE_MODE=.*\n?/m, '').replace(/^DEMO_POSTGRES_BIN=.*\n?/m, '')
  text += 'DEMO_DATABASE_MODE=native\nDEMO_POSTGRES_BIN=' + (process.env.DEMO_POSTGRES_BIN || '/opt/homebrew/opt/postgresql@17/bin') + '\n'
  writeFileSync(envPath, text, { mode: 0o600 })
}
const settings = Object.fromEntries(readFileSync(envPath, 'utf8').split('\n').filter(v => v && !v.startsWith('#')).map(v => { const i=v.indexOf('='); return [v.slice(0,i),v.slice(i+1)] }))
const env = { ...process.env, ...settings, Logging__LogLevel__Default: 'Warning', Logging__LogLevel__Microsoft: 'Warning' }
const compose = ['compose', '--env-file', envPath, '-f', join(root,'compose.demo.yaml')]
const native = settings.DEMO_DATABASE_MODE === 'native'
const pg = name => join(settings.DEMO_POSTGRES_BIN || '', name)
function databases() {
  if (!native) { run('docker', [...compose, 'up', '-d', '--wait'], env); return }
  for (const [name, port, password] of [['api',5441,settings.DEMO_API_DATABASE_PASSWORD],['runtime',5442,settings.DEMO_RUNTIME_DATABASE_PASSWORD]]) {
    const data = join(stateDirectory,`native-${name}`)
    const passwordFile = join(stateDirectory,`native-${name}.password`)
    if (!existsSync(join(data,'PG_VERSION'))) {
      writeFileSync(passwordFile,password+'\n',{mode:0o600})
      run(pg('initdb'),['-D',data,'-U','postgres','--auth-host=scram-sha-256','--auth-local=trust','--pwfile='+passwordFile],env)
    }
    if (spawnSync(pg('pg_ctl'),['-D',data,'status'],{stdio:'ignore'}).status !== 0)
      run(pg('pg_ctl'),['-D',data,'-l',join(stateDirectory,`postgres-${name}.log`),'-o',`-h 127.0.0.1 -p ${port} -k /private/tmp`,'-w','start'],env)
  }
  const apiEnv={...env,PGPASSWORD:settings.DEMO_API_DATABASE_PASSWORD}
  const exists=run(pg('psql'),['-h','127.0.0.1','-p','5441','-U','postgres','-d','postgres','-tAc',"SELECT count(*) FROM pg_database WHERE datname='erpgenerator_demo_api'"],apiEnv,true)
  if (exists === '0') run(pg('createdb'),['-h','127.0.0.1','-p','5441','-U','postgres','erpgenerator_demo_api'],apiEnv)
}
const statePath = join(stateDirectory, 'processes.json')
let processes = existsSync(statePath) ? JSON.parse(readFileSync(statePath,'utf8')) : {}
function alive(name) {
  const state = processes[name]
  if (!state) return false
  try { process.kill(state.pid, 0); return true } catch (error) {
    if (error.code === 'ESRCH') return false
    throw error
  }
}
function start(name, command, args, overrides = {}) {
  if (alive(name)) return
  const output = openSync(join(stateDirectory,`${name}.log`),'a',0o600)
  const child = spawn(command, args, { cwd: root, env: { ...env, Logging__LogLevel__Default: 'Warning', Logging__LogLevel__Microsoft: 'Warning', ...overrides }, detached: true, stdio: ['ignore',output,output] })
  child.unref()
  processes[name] = { pid: child.pid, marker: args.find(a => a.includes('ErpGenerator.')) || 'next start' }
  writeFileSync(statePath,JSON.stringify(processes,null,2))
}
async function wait(url, timeout = 60000) {
  const deadline=Date.now()+timeout
  while(Date.now()<deadline) {
    try { if ((await fetch(url, { signal: AbortSignal.timeout(2000) })).ok) return } catch {}
    await new Promise(r=>setTimeout(r,1000))
  }
  throw new Error(`Service did not become healthy: ${url}. Read .demo logs.`)
}
function binaries(application) { return join(root,application,'src',`ErpGenerator.${application === 'api' ? 'Api' : 'Runtime'}.WebHost`,'bin','Debug','net10.0',`ErpGenerator.${application === 'api' ? 'Api' : 'Runtime'}.WebHost.dll`) }
async function backends() {
  databases()
  if (!existsSync(binaries('api')) || !existsSync(binaries('runtime'))) throw new Error('Run npm run restore and npm run build first.')
  run('dotnet', [join(root,'api/src/ErpGenerator.Api.MigrationService/bin/Debug/net10.0/ErpGenerator.Api.MigrationService.dll')], env)
  start('api','dotnet',[binaries('api')],{ ASPNETCORE_URLS:'http://127.0.0.1:5214' })
  await wait('http://127.0.0.1:5214/health')
  start('runtime','dotnet',[binaries('runtime')],{ ASPNETCORE_URLS:'http://127.0.0.1:5039' })
  await wait('http://127.0.0.1:5039/health')
}
function counts() {
  const query=`SELECT "Status",count(*) FROM control_plane.demo_instances WHERE "CustomerId" IS NULL AND "Status" <> 'archived' GROUP BY "Status" ORDER BY "Status"`
  if (native) return run(pg('psql'),['-h','127.0.0.1','-p','5441','-U','postgres','-d','erpgenerator_demo_api','-tAc',query],{...env,PGPASSWORD:settings.DEMO_API_DATABASE_PASSWORD},true)
  return run('docker',[...compose,'exec','-T','api-postgres','psql','-U','postgres','-d','erpgenerator_demo_api','-tAc',query],env,true)
}

try {
  if(action==='setup') databases()
  else if(action==='prepare') {
    await backends()
    const target=Number(settings.Demo__PreparedCopies || 20)
    const deadline=Date.now()+15*60*1000
    while(Date.now()<deadline) {
      const result=counts()
      if(result.includes('failed|')) throw new Error(`A prepared copy failed acceptance. Read .demo/api.log and .demo/runtime.log. Pool: ${result}`)
      if(result.split('\n').includes('ready|'+target)) { console.log(`${target} fresh Dandelion copies passed ERP acceptance.`); break }
      console.log('Preparing copies:', result.replaceAll('\n',', ') || 'queued')
      await new Promise(r=>setTimeout(r,5000))
    }
    if(!counts().split('\n').includes('ready|'+target)) throw new Error('Preparation timed out; inspect .demo logs.')
  } else if(action==='start') {
    await backends()
    if(!existsSync(join(root,'portal/.next/BUILD_ID'))) throw new Error('Run npm run build first.')
    start('portal','node',[join(root,'portal/node_modules/next/dist/bin/next'),'start',join(root,'portal'),'-p','3001','-H','127.0.0.1'])
    await wait('http://localhost:3001/en/account')
    console.log('Portal: http://localhost:3001 — Runtime: tenant.localhost:5039')
  } else if(action==='stop') {
    const stopping = []
    for(const [name,state] of Object.entries(processes)) {
      if(!alive(name)) continue
      const command=run('ps',['-p',String(state.pid),'-o','command='],env,true)
      let owned = command.includes(root) && (name === 'portal' || command.includes(state.marker))
      // Next.js rewrites its process title; also verify its working directory.
      if (!owned && name === 'portal' && command.startsWith('next-server ')) {
        const cwd = process.platform === 'darwin'
          ? run('lsof',['-a','-p',String(state.pid),'-d','cwd','-Fn'],env,true).split('\n').find(v=>v.startsWith('n'))?.slice(1)
          : readlinkSync(`/proc/${state.pid}/cwd`)
        owned = cwd === root.replace(/\/$/,'')
      }
      if(!owned) throw new Error(`Refusing to stop an unrelated process recorded as ${name}.`)
      stopping.push(state.pid)
    }
    for (const pid of stopping) process.kill(-pid,'SIGTERM')
    writeFileSync(statePath,'{}')
    if (native) { for (const name of ['api','runtime']) run(pg('pg_ctl'),['-D',join(stateDirectory,`native-${name}`),'-m','fast','stop'],env) }
    else run('docker',[...compose,'stop'],env)
    console.log('Stopped rehearsal services. Database volumes and local secrets are preserved.')
  } else if(action==='health') {
    await wait('http://127.0.0.1:5214/health',5000);await wait('http://127.0.0.1:5039/health',5000)
    console.log('API and Runtime are healthy. Unassigned demo pool:\n'+counts())
    console.log('Live generation credential:',(env.Claude__ApiKey || env.ANTHROPIC_API_KEY)?'configured':'pending');console.log('Live Notion credential:',env.Nango__SecretKey?'configured':'pending')
  } else throw new Error('Expected setup, prepare, start, stop, or health.')
} catch(error) { console.error(error.message);process.exitCode=1 }
