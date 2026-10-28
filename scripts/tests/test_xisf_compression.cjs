const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

for (const name of ['PixInsightTemplate.pjsr', 'PixInsightTemplate.DARKS.pjsr']) {
  const source = fs.readFileSync(path.join(__dirname, '../../src/FlatMaster.Infrastructure/Services', name), 'utf8');
  const helpers = source.match(/^function parentDir\(p\).*$/m)[0] + '\n' +
    source.match(/^function ensureDir\(p\)\{[\s\S]*?^\}/m)[0];
  const region = source.substring(source.indexOf('function normalizeXisfHints'), source.indexOf('function safeNum'));
  assert(region.includes('function saveCompressedXisf') && region.includes('function saveImage'), name + ' markers');

  function createContext({ failCompressed = false, failWrite = false } = {}) {
    const state = { files: new Set(), created: [], saveAs: [], warnings: [], closed: 0 };
    const context = vm.createContext({
      state,
      log() {}, warn: message => state.warnings.push(String(message)),
      File: {
        directoryExists: () => true,
        exists: p => state.files.has(p),
        remove: p => state.files.delete(p)
      },
      FileInfo: function (p) { this.size = state.files.has(p) ? 1234 : 0; },
      FileFormat: function (id, toRead, toWrite) { this.id = id; this.toRead = toRead; this.toWrite = toWrite; },
      ImageDescription: function () {},
      FileFormatInstance: function (format) {
        const instance = this;
        instance.format = format;
        instance.create = (p, hints) => {
          if (failCompressed) throw new Error('unsupported hints');
          state.created.push({ path: p, hints, format });
          state.current = p;
          return true;
        };
        instance.setOptions = d => { state.options = d; };
        instance.writeImage = image => { if (failWrite) return false; state.files.add(state.current); state.written = image; return true; };
        instance.close = () => { state.closed++; };
      }
    });
    vm.runInContext(helpers + '\n' + region, context);
    return context;
  }

  function createWindow({ real = true, bits = 64, color = false } = {}) {
    const image = { width: 9576, height: 6388, numberOfChannels: color ? 3 : 1, isColor: color, bitsPerSample: bits, isReal: real };
    return {
      image, keywords: ['IMAGETYP'],
      mainView: { image },
      saveAs(...args) { this.args = args; this.state.files.add(args[0]); }
    };
  }

  // Semicolon-separated hints from older configurations are normalised to the whitespace form PixInsight reads.
  let context = createContext();
  assert.equal(context.normalizeXisfHints('compression-codec zlib+sh; compression-level 9; checksum sha1'),
    'compression-codec zlib+sh compression-level 9 checksum sha1');
  assert.equal(context.normalizeXisfHints('  compression-codec   zstd+sh '), 'compression-codec zstd+sh');
  assert.equal(context.normalizeXisfHints(undefined), '');

  // Float64 mono master: compressed through FileFormatInstance with the window's own sample format.
  context = createContext();
  let win = createWindow(); win.state = context.state;
  context.saveImage(win, 'D:/out/master.xisf', 'compression-codec zstd+sh; compression-level 9', 'xisf');
  assert.equal(context.state.created.length, 1);
  assert.equal(context.state.created[0].hints, 'compression-codec zstd+sh compression-level 9');
  assert.equal(context.state.created[0].format.id, 'XISF');
  assert.equal(context.state.options.bitsPerSample, 64);
  assert.equal(context.state.options.ieeefpSampleFormat, true);
  assert.equal(context.state.options.width, 9576);
  assert.equal(context.state.options.numberOfChannels, 1);
  assert.equal(context.state.options.colorSpace, 0);
  assert.equal(win.args, undefined, 'saveAs must not be used when compressed output succeeded');
  assert.equal(context.state.closed, 1);

  // RGB and integer images keep their format too.
  context = createContext();
  win = createWindow({ real: true, bits: 32, color: true }); win.state = context.state;
  context.saveImage(win, 'D:/out/rgb.xisf', 'compression-codec zstd+sh', 'xisf');
  assert.equal(context.state.options.bitsPerSample, 32);
  assert.equal(context.state.options.numberOfChannels, 3);
  assert.equal(context.state.options.colorSpace, 1);
  context = createContext();
  win = createWindow({ real: false, bits: 16 }); win.state = context.state;
  context.saveImage(win, 'D:/out/u16.xisf', 'compression-codec zstd+sh', 'xisf');
  assert.equal(context.state.options.ieeefpSampleFormat, false);

  // No hints, or FITS output: unchanged plain saveAs behaviour.
  for (const [hints, ext] of [['', 'xisf'], ['compression-codec zstd+sh', 'fits']]) {
    context = createContext();
    win = createWindow(); win.state = context.state;
    context.saveImage(win, 'D:/out/plain.' + ext, hints, ext);
    assert.equal(context.state.created.length, 0);
    assert(win.args, 'saveAs must be used');
  }

  // A compressed save that fails must never lose the output: the partial file is removed and saveAs is used.
  for (const options of [{ failCompressed: true }, { failWrite: true }]) {
    context = createContext(options);
    win = createWindow(); win.state = context.state;
    context.saveImage(win, 'D:/out/fallback.xisf', 'compression-codec zstd+sh', 'xisf');
    assert(win.args, 'fallback saveAs must run');
    assert(context.state.warnings.some(w => w.includes('compressed save failed')));
    assert(context.state.files.has('D:/out/fallback.xisf'));
  }
  console.log('PASS: ' + name + ' - compressed XISF save, hint normalisation, sample format, and fallback');
}
