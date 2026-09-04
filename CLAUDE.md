@AGENTS.md

# 言語・コミュニケーション

- ユーザー向けの応答は自然な日本語で行う。
- 見出し、箇条書き、番号付き選択肢の説明も日本語で記述する。
- 提案、確認事項、警告、エラー説明も日本語で記述する。
- コード識別子、関数名、クラス名、RVA、VA、ログ文字列、API名など、原文維持が必要な技術情報のみ英語を許可する。
- 「1. Analysis」「2. Findings」のようなユーザー向け英語見出しを使用しない。
- 技術用語を英語で記載する場合は、必要に応じて日本語の説明を添える。

# NocturneModernGameplay 作業開始ルール

作業開始時に、次の順番で読む。

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`
3. 今回のactive investigationの`PLAN.md`

現在のpresentation調査では、`investigations/PRESENTATION_CONSUMER/PLAN.md`を読む。

参照の優先順位は次のとおり。

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`
3. active investigation
4. 必要な場合のみ`research/`

- `01_CURRENT_STATE.md`を現在承認済みのCanonical Stateとして扱う。
- `research/`の古い資料に`01_CURRENT_STATE.md`と矛盾する記述があっても、その古い記述を自動的に復活させない。
- REJECTED事項を再採用する場合は、新しいEvidenceを示してユーザーへ確認する。

# Evidence Discipline

- `00_PROJECT_RULES.md`のEvidence Disciplineを尊重する。
- `CONFIRMED`、`STRONGLY SUPPORTED`、`HYPOTHESIS`、`REJECTED`、`UNRESOLVED`を明確に区別する。
- `HYPOTHESIS`を新しいEvidenceなしに`CONFIRMED`へ昇格させない。
