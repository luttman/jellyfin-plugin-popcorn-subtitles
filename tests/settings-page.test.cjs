const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const html = fs.readFileSync(path.join(__dirname, '../Jellyfin.Plugin.PopcornSubtitles/Configuration/config.html'), 'utf8');
const script = html.match(/<script[^>]*>([\s\S]*?)<\/script>/)[1];
const elements = new Map();
for (const id of html.matchAll(/id="([^"]+)"/g)) {
  elements.set('#' + id[1], { value: '', checked: false, disabled: false, events: {}, addEventListener(name, action) { this.events[name] = action; } });
}
const page = elements.get('#PopcornSubtitlesPage');
page.querySelector = selector => elements.get(selector);
const alerts = [];
const saves = [];
let failSave = false;
let lastTest;
const original = { Enabled: true, ManifestUrl: 'https://example.test/langs=swe,eng&ai=on/manifest.json', ExtraSetting: 'keep' };
const context = vm.createContext({
  URL,
  document: { querySelector: selector => elements.get(selector) },
  ApiClient: {
    getPluginConfiguration: async () => ({ ...original }),
    updatePluginConfiguration: async (_, config) => { if (failSave) throw new Error('Save failed'); saves.push(config); return {}; },
    getUrl: route => '/' + route,
    ajax: async options => { lastTest = JSON.parse(options.data); return { count: 1, tracks: [{ language: 'eng', label: '<b>NTb</b>' }] }; }
  },
  Dashboard: { alert: message => alerts.push(message), processPluginConfigurationUpdateResult: () => {} }
});
(async () => {
  vm.runInContext(script, context);
  await page.events.pageshow();
  assert.equal(elements.get('#PopcornMovieLanguages').value, 'swe,eng');
  assert.equal(elements.get('#PopcornSeriesGroups').value, '');
  assert.equal(elements.get('#PopcornAiNotes').checked, false);
  assert.equal(elements.get('#PopcornSearchGroups').checked, false);
  assert.equal(elements.get('#PopcornLimit').value, 4);
  elements.get('#PopcornMovieLanguages').value = 'ENG, swe, eng';
  elements.get('#PopcornMovieGroups').value = 'FraMeSToR; FGT';
  elements.get('#PopcornSeriesLanguages').value = 'dan';
  elements.get('#PopcornSeriesGroups').value = 'NTb, FLUX';
  const submit = () => elements.get('#PopcornSubtitlesForm').events.submit({ preventDefault() {} });
  await submit();
  assert.equal(saves.length, 1);
  assert.equal(saves[0].MovieLanguages, 'eng,swe');
  assert.equal(saves[0].MovieReleaseGroups, 'FraMeSToR,FGT');
  assert.equal(saves[0].SeriesLanguages, 'dan');
  assert.equal(saves[0].SeriesReleaseGroups, 'NTb,FLUX');
  assert.equal(saves[0].ExtraSetting, 'keep');
  assert.equal(saves[0].EnableAiNotes, false);
  elements.get('#PopcornTestType').value = 'series';
  elements.get('#PopcornTestId').value = 'tt0903747:1:3';
  elements.get('#PopcornTestFilename').value = 'Episode-NTb.mkv';
  elements.get('#PopcornSearchGroups').checked = true;
  elements.get('#PopcornLimit').value = '7';
  await elements.get('#PopcornTest').events.click.call(elements.get('#PopcornTest'));
  assert.equal(lastTest.Type, 'series');
  assert.equal(lastTest.Configuration.ResultsPerLanguage, 7);
  assert.equal(lastTest.Configuration.SearchReleaseGroups, true);
  assert.equal(saves.length, 1, 'Test search must not save configuration');
  assert.match(elements.get('#PopcornTestResult').textContent, /<b>NTb<\/b>/, 'Labels must be rendered as text, not HTML');
  elements.get('#PopcornMovieLanguages').value = 'sv';
  await submit();
  assert.equal(saves.length, 1);
  assert.match(alerts.pop(), /three-letter codes/);
  assert.equal(elements.get('#PopcornSave').disabled, false);
  elements.get('#PopcornMovieLanguages').value = 'swe';
  failSave = true;
  await submit();
  assert.equal(alerts.pop(), 'Save failed');
  assert.equal(elements.get('#PopcornSave').disabled, false);
  // Reopening the plugin page must not fail on redeclared global const bindings.
  vm.runInContext(script, context);
  console.log('PASS: settings load, migration defaults, separate values, validation, save errors and reopen.');
})().catch(error => { console.error(error); process.exitCode = 1; });
