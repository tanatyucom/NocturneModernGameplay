# NocturneModernGameplay Roadmap

## Project Philosophy

NocturneModernGameplayは、SMT3の世界観・戦闘・基本設計を維持しながら、
難易度、確率、育成効率、リソース管理などゲーム結果に関わる部分を現代化します。
SMT5以降に近い快適性を取り入れますが、「何でも入りQoL Pack」にはしません。

各機能は可能な限り独立してON/OFFできることを前提とし、最小で可逆な変更、
静的・native解析を先行する方針、fail-closedな挙動を重視します。

## Controller vs Gameplay

### NocturneModernController

入力、操作、カメラ、ショートカットなどを現代化します。ゲーム難易度や結果そのものは
変更しません。

- Right Stick Camera
- Dash
- Quick Heal
- Force Encounter
- Smart Auto Battle
- Context-aware Key Bindings
- Settings GUI

Quick Healはゲーム内で習得済みの回復スキルと実際のMPを使い、操作だけを短縮します。
Force Encounterも通常エンカウント可能というnative判定を利用して、戦闘開始操作だけを
短縮します。このため、どちらもController側の責任です。

### NocturneModernGameplay

難易度、確率、進行、育成効率、報酬、リソース管理など、ゲーム結果に影響する変更を
担当します。Controller側の入力・操作機能は再実装しません。

## Community Mod Compatibility Policy

他作者が既に実装し、コミュニティで利用可能な機能は原則として再実装しません。
既存の解決策を尊重し、競合と不要な重複を避け、このプロジェクト独自の価値を明確にします。

既存MODを調査する場合も、第三者のコードをコピーまたは無断再利用しません。ライセンスと
配布条件を個別に確認します。

## In Development

### Skill Mutation: Learn As New

バニラでは、スキル変化が発生すると元スキルが変化後スキルへ置き換えられます。
本機能は元スキルを残し、変化後スキルを新規習得として扱います。

```text
Vanilla:
Original Skill -> Mutated Skill

NocturneModernGameplay:
Original Skill remains + Mutated Skill is learned
```

- 空き枠がある場合の新規追加: 動作確認済み
- 8枠満杯時のnative忘却UI利用: 開発・実機テスト中
- Inline Transaction Architecture: 実験・開発中
- 完成・正式公開済みとはまだ扱わない

現在のQueue方式はLearn-As-Newの成立を検証した機能的プロトタイプです。
Inline Transactionが安定しfallbackが不要と確認された後に限り、Queue固有コードを
段階的に廃止する予定です。現時点でQueueを即時削除する予定はありません。

## Planned

### Rare Chest: Guaranteed Rare

満月でなくてもレア宝箱を確定扱いにし、月齢待ちとランダム性を減らします。
確率と報酬効率に影響するためGameplay側の機能です。

### Terminal Full Recovery

ターミナル利用時にHPとMPを全回復し、SMT5のLeyline Fountに近いテンポを目指します。
リソース管理難易度を変えるためGameplay側で扱います。

### Quick Return

ダンジョンなどからの帰還を簡略化します。帰還手段、消費、探索リスクに影響するため
Gameplay側の候補ですが、既存MODとの重複確認後に実装可否を決めます。

**Status:** Planned, pending existing-mod overlap check.

### Repeatable Skill Power-Up

Skill Power-Upを繰り返し可能にし、育成自由度を高めます。ゲームバランスに影響するため
Gameplay側で、独立したON/OFF機能として扱います。

## Possible / Later

### Convenience Multipliers

- EXP Multiplier
- Macca Multiplier
- Drop Rate Multiplier
- Sell Value Multiplier

SMT5化の中核ではなく、周回や時間短縮向けのoptional convenience settingsです。
将来GUIへ追加する場合も、Modern Gameplay本体とは別カテゴリにする可能性があります。

### Level-Up Full Recovery

レベルアップ時にHPとMPを全回復する案です。戦闘継続力へ直接影響し、Terminal Full
Recoveryだけで十分な可能性もあるため、現時点では実装確定ではありません。

### Fusion-related QoL

skill inheritance QoL、fusion result previewなど、SMT5系に近い合体・育成の快適化です。
既存MODとの重複を必ず確認してから検討し、v1系の中核機能には含めません。

## Out of Scope / Already Covered

### Controller側で担当済み

- Right Stick Camera
- Dash
- Quick Heal
- Force Encounter
- Smart Auto Battle
- Key Bindings / Input Shortcuts
- Controller Settings GUI

### 既存コミュニティMODを優先する領域

- Save Anywhere
- Encounter Toggle
- Suspend / Continue関連QoL
- Moon Phase Control関連QoL

Existing community solutions are preferred over duplicating functionality.

## Development Priorities

1. **Skill Mutation: Learn As New**
   - Inline Transactionの完成
   - 8枠満杯時の忘却フロー検証
   - Inlineが安定し不要と確認された後にのみQueue fallbackを段階的に廃止
2. **Rare Chest: Guaranteed Rare**
3. **Terminal Full Recovery**
4. **Quick Return**
5. **Repeatable Skill Power-Up**
6. **Convenience Multipliers**
7. **Additional Gameplay Features** — 既存MODとの重複確認後にのみ追加
