#!/usr/bin/env bash
#
# create-ado-story.sh — Create a tagged ADO story for end-to-end testing.
#
# Creates an Azure DevOps work item (User Story by default) pre-tagged with
# `repo:<key>` and `agent-ready`, plus acceptance criteria, so the full
# lifecycle (discovery → pi job → PR) can be exercised without manual board setup.
#
# Usage:
#   ./dev/create-ado-story.sh [OPTIONS]
#
# Options:
#   -t, --title TITLE          Work item title (required)
#   -r, --repo-key KEY         Repository profile key for repo: tag (default: agent-router)
#   -p, --project PROJECT      ADO project name (default: from env ADO_PROJECT or "Projecto")
#   -o, --org ORG              ADO organization name (default: from env ADO_ORG or "rpollard0630")
#   -w, --work-item-type TYPE  Work item type (default: User Story)
#   -d, --description TEXT     Description text (default: derived from title)
#   -a, --acceptance TEXT      Acceptance criteria as semicolon-separated items
#                              (default: "Verify the feature works as described")
#   -s, --state STATE          Initial state (default: New)
#   --dry-run                  Print the curl command without executing
#
# Environment variables:
#   AZURE_DEVOPS_PAT           Personal Access Token with "Work items: Read & write" scope
#   ADO_ORG                    Default organization name (override with -o)
#   ADO_PROJECT                Default project name (override with -p)
#
# Examples:
#   # Create a simple test story with defaults
#   ./dev/create-ado-story.sh --title "Add greeting endpoint"
#
#   # Create a story with custom repo key and acceptance criteria
#   ./dev/create-ado-story.sh \
#     --title "Implement user authentication" \
#     --repo-key my-service \
#     --acceptance "Handles valid tokens; Rejects expired tokens; Logs auth failures"
#
#   # Dry run to inspect the API call
#   ./dev/create-ado-story.sh --title "Test story" --dry-run

set -euo pipefail

# ─── Defaults ─────────────────────────────────────────────────────────────────

TITLE=""
REPO_KEY="test1"
PROJECT="${ADO_PROJECT:-Projecto}"
ORG="${ADO_ORG:-rpollard0630}"
WORK_ITEM_TYPE="User Story"
DESCRIPTION=""
ACCEPTANCE=""
STATE="New"
DRY_RUN=false

# ─── Argument parsing ────────────────────────────────────────────────────────

usage() {
    sed -n '2,/^$/s/^# \?//p' "$0"
    exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -t|--title)
            TITLE="$2"; shift 2 ;;
        -r|--repo-key)
            REPO_KEY="$2"; shift 2 ;;
        -p|--project)
            PROJECT="$2"; shift 2 ;;
        -o|--org)
            ORG="$2"; shift 2 ;;
        -w|--work-item-type)
            WORK_ITEM_TYPE="$2"; shift 2 ;;
        -d|--description)
            DESCRIPTION="$2"; shift 2 ;;
        -a|--acceptance)
            ACCEPTANCE="$2"; shift 2 ;;
        -s|--state)
            STATE="$2"; shift 2 ;;
        --dry-run)
            DRY_RUN=true; shift ;;
        -h|--help)
            usage 0 ;;
        *)
            echo "Unknown option: $1" >&2
            usage 1 ;;
    esac
done

# ─── Validation ──────────────────────────────────────────────────────────────

if [[ -z "$TITLE" ]]; then
    echo "Error: --title is required." >&2
    usage 1
fi

PAT="${AZURE_DEVOPS_PAT:-}"
if [[ -z "$PAT" ]]; then
    echo "Error: AZURE_DEVOPS_PAT is not set." >&2
    echo "Set it via your .env file or export AZURE_DEVOPS_PAT=your_token" >&2
    exit 1
fi

# ─── Derive description if not provided ──────────────────────────────────────

if [[ -z "$DESCRIPTION" ]]; then
    DESCRIPTION="Auto-generated test story for agent router end-to-end lifecycle testing.

Title: ${TITLE}
Repo key: ${REPO_KEY}
Created: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
fi

# ─── Build acceptance criteria JSON ──────────────────────────────────────────
# ADO expects acceptance criteria as a JSON object with numeric string keys.
# Input: semicolon-separated items (or a single string with no semicolons).

build_acceptance_json() {
    local input="$1"
    local json="{"
    local index=1
    local IFS=';'

    for item in $input; do
        item="$(echo "$item" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        if [[ -n "$item" ]]; then
            # Escape double quotes in the criterion text
            item="${item//\"/\\\"}"
            if [[ $index -gt 1 ]]; then
                json+=","
            fi
            json+="\"${index}\":\"${item}\""
            ((index++))
        fi
    done

    json+="}"
    echo "$json"
    echo $((index - 1))
}

if [[ -z "$ACCEPTANCE" ]]; then
    ACCEPTANCE="Verify the feature works as described"
fi

ACCEPTANCE_OUTPUT="$(build_acceptance_json "$ACCEPTANCE")"
ACCEPTANCE_JSON="$(echo "$ACCEPTANCE_OUTPUT" | head -1)"
ACCEPTANCE_COUNT="$(echo "$ACCEPTANCE_OUTPUT" | tail -1)"

# ─── Build the JSON-Patch body ───────────────────────────────────────────────

# Tags: agent-ready (eligibility) + repo:{key} (repository association)
TAGS="agent-ready; repo:${REPO_KEY}"

# Escape values for JSON embedding
escape_json() {
    local val="$1"
    val="${val//\\/\\\\}"
    val="${val//\"/\\\"}"
    val="${val//$'\n'/\\n}"
    val="${val//$'\r'/}"
    val="${val//$'\t'/\\t}"
    echo "$val"
}

TITLE_ESCAPED="$(escape_json "$TITLE")"
DESCRIPTION_ESCAPED="$(escape_json "$DESCRIPTION")"

BODY="[
  { \"op\": \"add\", \"path\": \"/fields/System.Title\", \"value\": \"${TITLE_ESCAPED}\" },
  { \"op\": \"add\", \"path\": \"/fields/System.Description\", \"value\": \"${DESCRIPTION_ESCAPED}\" },
  { \"op\": \"add\", \"path\": \"/fields/System.Tags\", \"value\": \"${TAGS}\" },
  { \"op\": \"add\", \"path\": \"/fields/System.State\", \"value\": \"${STATE}\" },
  { \"op\": \"add\", \"path\": \"/fields/Microsoft.VSTS.Common.AcceptanceCriteria\", \"value\": ${ACCEPTANCE_JSON} }
]"

# ─── URL encoding helper ─────────────────────────────────────────────────────
# Encodes spaces and reserved characters for safe use in URL path segments.
# Uses printf to percent-encode each byte that is not unreserved (RFC 3986).

urlencode() {
    local str="$1"
    local len=${#str}
    local encoded=""
    local i char hex

    for (( i=0; i<len; i++ )); do
        char="${str:$i:1}"
        case "$char" in
            [a-zA-Z0-9._-~])
                encoded+="$char"
                ;;
            *)
                hex="$(printf '%%%02X' "'$char")"
                encoded+="$hex"
                ;;
        esac
    done

    echo "$encoded"
}

# ─── Construct the API URL ───────────────────────────────────────────────────

BASE_URL="https://dev.azure.com/${ORG}"
WORK_ITEM_TYPE_ENCODED="$(urlencode "$WORK_ITEM_TYPE")"
API_URL="${BASE_URL}/${PROJECT}/_apis/wit/workitems/${WORK_ITEM_TYPE_ENCODED}?api-version=7.1"

# ─── Execute ─────────────────────────────────────────────────────────────────

AUTH_HEADER="$(printf ':%s' "$PAT" | base64 -w 0)"

if [[ "$DRY_RUN" == true ]]; then
    echo "=== DRY RUN ==="
    echo "URL: $API_URL"
    echo "Body:"
    echo "$BODY" | python3 -m json.tool 2>/dev/null || echo "$BODY"
    echo ""
    echo "curl -s -X POST \"$API_URL\" \\"
    echo "  -H \"Authorization: Basic <base64>\" \\"
    echo "  -H \"Content-Type: application/json-patch+json\" \\"
    echo "  -d '$BODY'"
    exit 0
fi

echo "Creating ADO work item..."
echo "  Org:              $ORG"
echo "  Project:          $PROJECT"
echo "  Type:             $WORK_ITEM_TYPE"
echo "  Title:            $TITLE"
echo "  Tags:             $TAGS"
echo "  State:            $STATE"
echo "  Acceptance items: $ACCEPTANCE_COUNT"
echo ""

RESPONSE="$(curl -s -w "\n%{http_code}" -X POST "$API_URL" \
    -H "Authorization: Basic ${AUTH_HEADER}" \
    -H "Content-Type: application/json-patch+json" \
    -d "$BODY")"

HTTP_CODE="$(echo "$RESPONSE" | tail -1)"
BODY_RESPONSE="$(echo "$RESPONSE" | head -n -1)"

if [[ "$HTTP_CODE" =~ ^2 ]]; then
    # Extract the work item ID from the response
    ITEM_ID="$(echo "$BODY_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id', 'unknown'))" 2>/dev/null || echo "unknown")"
    ITEM_URL="${BASE_URL}/${PROJECT}/_workitems/edit/${ITEM_ID}"

    echo "✓ Work item created successfully!"
    echo "  ID:           $ITEM_ID"
    echo "  URL:          $ITEM_URL"
    echo ""
    echo "Next steps:"
    echo "  1. Ensure agent router is running: export DOTNET_ENVIRONMENT=Live.ADO && dotnet run --project src/AgentController.Api"
    echo "  2. Watch logs for discovery of work item #$ITEM_ID"
    echo "  3. Check the board: $ITEM_URL"
else
    echo "✗ Failed to create work item (HTTP $HTTP_CODE)" >&2
    echo "$BODY_RESPONSE" >&2
    exit 1
fi
