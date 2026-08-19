import { readdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, extname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { brotliCompress, constants, gzip } from 'node:zlib'

const compressBrotli = promisify(brotliCompress)
const compressGzip = promisify(gzip)
const scriptDirectory = dirname(fileURLToPath(import.meta.url))
const assetsDirectory = join(scriptDirectory, '..', '..', 'src', 'AStockMonitor.Api', 'wwwroot', 'assets')
const compressible = new Set(['.js', '.css', '.json', '.svg', '.woff2'])

for (const entry of await readdir(assetsDirectory, { withFileTypes: true })) {
  if (!entry.isFile() || !compressible.has(extname(entry.name))) continue
  const sourcePath = join(assetsDirectory, entry.name)
  const source = await readFile(sourcePath)
  if (source.length < 1024) continue
  const [brotli, gzipped] = await Promise.all([
    compressBrotli(source, { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } }),
    compressGzip(source, { level: 9 }),
  ])
  await Promise.all([
    writeFile(`${sourcePath}.br`, brotli),
    writeFile(`${sourcePath}.gz`, gzipped),
  ])
}
