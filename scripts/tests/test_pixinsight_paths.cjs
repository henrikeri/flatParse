const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

for (const name of ['PixInsightTemplate.pjsr', 'PixInsightTemplate.DARKS.pjsr']) {
  const source = fs.readFileSync(path.join(__dirname, '../../src/FlatMaster.Infrastructure/Services', name), 'utf8');
  const helpers = source.match(/^function parentDir\(p\).*$/m)[0] + '\n' +
    source.match(/^function ensureDir\(p\)\{[\s\S]*?^\}/m)[0];
  const directories = new Set(['//tower.local/Astrofoto', 'C:/']);
  const created = [];
  const context = vm.createContext({ File: {
    directoryExists: p => directories.has(p),
    createDirectory: (p, recursive) => {
      assert.equal(recursive, false, 'UNC creation must never recurse through the server root');
      assert(!/^\/\/[^/]+(?:\/[^/]+)?$/.test(p), 'Server/share roots must never be created');
      const parent = p.slice(0, p.lastIndexOf('/'));
      assert(directories.has(parent) || directories.has(parent + '/'), 'Parent must already exist');
      directories.add(p);
      created.push(p);
    }
  }});
  vm.runInContext(helpers, context);
  const target = '//tower.local/Astrofoto/0000 To Be Sorted/New Flats/BLUE/_CalibratedFlats_30s';
  context.ensureDir(target);
  assert(directories.has(target));
  assert.equal(created.length, 4);
  context.ensureDir(target + '/');
  assert.equal(created.length, 4, 'Existing directories must not be recreated');
  context.ensureDir('\\\\tower.local\\Astrofoto\\folder with spaces\\child');
  assert(directories.has('//tower.local/Astrofoto/folder with spaces/child'));
  context.ensureDir('C:/new directory/child');
  assert(directories.has('C:/new directory/child'));
  const before = created.length;
  assert.throws(() => context.ensureDir('//unavailable/share/child'), /network share is unavailable/);
  assert.throws(() => context.ensureDir('//unavailable'), /network share is unavailable/);
  assert.equal(created.length, before, 'Unavailable roots must produce no writes');
  console.log('PASS: ' + name + ' — UNC paths, spaces, existing directories, drive roots, and unavailable shares');
}
