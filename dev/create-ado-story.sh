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
#   -r, --repo-key KEY         Repository profile key for repo: tag (default: test1)
#   -p, --project PROJECT      ADO project name (default: from env ADO_PROJECT or "Projecto")
#   -o, --org ORG              ADO organization name (default: from env ADO_ORG or "rpollard0630")
#   -w, --work-item-type TYPE  Work item type (default: User Story)
#   -d, --description TEXT     Description text (default: derived from title)
#   -a, --acceptance TEXT      Acceptance criteria as semicolon-separated items
#                              (default: "Verify the feature works as described")
#   -s, --state STATE          Initial state (default: New)
#   --dry-run                  Print the curl command without executing
#   --debug                    Print request URLs, redacted headers, and raw responses
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
DEBUG=false

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
        --debug)
            DEBUG=true; shift ;;
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

# ─── Build acceptance criteria HTML ──────────────────────────────────────────
# ADO's AcceptanceCriteria field is an HTML field. Build an <ol><li>...</li></ol>
# string from semicolon-separated items. Each item is HTML-escaped before embedding.
# Output: line 1 = HTML string, line 2 = count of items.

html_escape() {
    local val="$1"
    # Use sed for HTML escaping — bash parameter expansion mishandles < > as redirection
    printf '%s' "$val" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g"
}

build_acceptance_html() {
    local input="$1"
    local html="<ol>"
    local count=0
    local IFS=';'

    for item in $input; do
        item="$(echo "$item" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        if [[ -n "$item" ]]; then
            html+="<li>$(html_escape "$item")</li>"
            ((count++))
        fi
    done

    html+="</ol>"
    echo "$html"
    echo "$count"
}

if [[ -z "$ACCEPTANCE" ]]; then
    ACCEPTANCE="Verify the feature works as described"
fi

ACCEPTANCE_OUTPUT="$(build_acceptance_html "$ACCEPTANCE")"
ACCEPTANCE_HTML="$(echo "$ACCEPTANCE_OUTPUT" | head -1)"
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
ACCEPTANCE_ESCAPED="$(escape_json "$ACCEPTANCE_HTML")"

BODY="[
  { \"op\": \"add\", \"path\": \"/fields/System.Title\", \"value\": \"${TITLE_ESCAPED}\" },
  { \"op\": \"add\", \"path\": \"/fields/System.Description\", \"value\": \"${DESCRIPTION_ESCAPED}\" },
  { \"op\": \"add\", \"path\": \"/fields/System.Tags\", \"value\": \"${TAGS}\" },
  { \"op\": \"add\", \"path\": \"/fields/System.State\", \"value\": \"${STATE}\" },
  { \"op\": \"add\", \"path\": \"/fields/Microsoft.VSTS.Common.AcceptanceCriteria\", \"value\": \"${ACCEPTANCE_ESCAPED}\" }
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
API_URL="${BASE_URL}/${PROJECT}/_apis/wit/workitems/\$${WORK_ITEM_TYPE_ENCODED}?api-version=7.1"

# ─── Preflight helpers ───────────────────────────────────────────────────────

# Check if a response body looks like HTML (not JSON).
# Returns 0 if HTML detected, 1 otherwise.
is_html_response() {
    local body="$1"
    # HTML responses start with < (possibly after whitespace/BOM)
    local trimmed
    trimmed="$(echo "$body" | sed 's/^[[:space:]]*//')"
    [[ "$trimmed" == "<"* ]]
}

# Check if a response body is valid JSON.
# Returns 0 if valid JSON, 1 otherwise.
is_valid_json() {
    local body="$1"
    echo "$body" | python3 -c "import sys,json; json.load(sys.stdin)" 2>/dev/null
}

# Render a concise error for non-JSON (HTML) responses.
# Prints the first meaningful HTML line (truncated) and targeted hints.
# Usage: render_non_json_error <body> <http_code>
render_non_json_error() {
    local body="$1"
    local http_code="${2:-unknown}"
    local first_line

    # Extract the first meaningful HTML tag (skip doctype, comments, whitespace)
    first_line="$(echo "$body" | grep -m1 '<[a-zA-Z]' | head -c 200 || true)"

    echo "  Received HTML instead of JSON (HTTP $http_code)." >&2
    echo "  ADO served the web frontend — this is an auth or routing problem." >&2
    if [[ -n "$first_line" ]]; then
        echo "  First HTML line: ${first_line}" >&2
    fi
    echo "" >&2
    echo "  Troubleshooting:" >&2
    echo "    1. Check AZURE_DEVOPS_PAT is valid and has 'Work items: Read & write' scope." >&2
    echo "    2. Verify org name '$ORG' (no trailing slashes, correct spelling)." >&2
    echo "    3. Verify project name '$PROJECT' exists in org '$ORG'." >&2
    echo "    4. Ensure Accept: application/json header is present in the request." >&2
}

# Perform a GET request and return HTTP code + body separated by last newline.
# Usage: ado_get <url>
ado_get() {
    local url="$1"
    if [[ "$DEBUG" == true ]]; then
        echo "[DEBUG] GET $url" >&2
        echo "[DEBUG] Headers: Authorization: Basic <redacted>, Accept: application/json" >&2
    fi
    local response
    response="$(curl -s -w "\n%{http_code}" -X GET "$url" \
        -H "Authorization: Basic ${AUTH_HEADER}" \
        -H "Accept: application/json")"
    if [[ "$DEBUG" == true ]]; then
        local code body
        code="$(echo "$response" | tail -1)"
        body="$(echo "$response" | head -n -1)"
        echo "[DEBUG] Response HTTP $code:" >&2
        echo "$body" >&2
    fi
    echo "$response"
}

# ─── Preflight checks ────────────────────────────────────────────────────────

run_preflights() {
    echo "Running preflight checks..."

    # ── Preflight 1: Verify org + project + PAT ──────────────────────────────
    local pf1_url="https://dev.azure.com/${ORG}/_apis/projects/${PROJECT}?api-version=7.1"
    local pf1_response pf1_code pf1_body

    pf1_response="$(ado_get "$pf1_url")"
    pf1_code="$(echo "$pf1_response" | tail -1)"
    pf1_body="$(echo "$pf1_response" | head -n -1)"

    if is_html_response "$pf1_body"; then
        echo "✗ Preflight FAILED: received HTML from ADO (auth/routing problem)" >&2
        echo "  URL: $pf1_url" >&2
        echo "  Hint: Check AZURE_DEVOPS_PAT is valid and has 'Work items: Read & write' scope." >&2
        echo "  Hint: Verify org name '$ORG' is correct (no trailing slashes)." >&2
        exit 1
    fi

    if [[ ! "$pf1_code" =~ ^2 ]]; then
        case "$pf1_code" in
            401|403)
                echo "✗ Preflight FAILED: authentication error (HTTP $pf1_code)" >&2
                echo "  Hint: AZURE_DEVOPS_PAT is invalid, expired, or lacks scope." >&2
                exit 1
                ;;
            404)
                echo "✗ Preflight FAILED: project not found (HTTP $pf1_code)" >&2
                echo "  Hint: Verify project name '$PROJECT' exists in org '$ORG'." >&2
                exit 1
                ;;
            000)
                echo "✗ Preflight FAILED: request never completed (HTTP $pf1_code)" >&2
                echo "  Request-level failure — got no HTTP response from the server." >&2
                echo "  Hint: Check URL, network connectivity, and curl availability." >&2
                echo "  URL: $pf1_url" >&2
                exit 1
                ;;
            *)
                echo "✗ Preflight FAILED: unexpected response (HTTP $pf1_code)" >&2
                echo "  URL: $pf1_url" >&2
                echo "$pf1_body" >&2
                exit 1
                ;;
        esac
    fi

    # Verify the response actually contains the expected project name
    local pf1_project_name
    pf1_project_name="$(echo "$pf1_body" | python3 -c "import sys,json; print(json.load(sys.stdin).get('name',''))" 2>/dev/null || echo "")"
    if [[ -n "$pf1_project_name" && "$pf1_project_name" != "$PROJECT" ]]; then
        echo "✗ Preflight WARNING: API returned project '$pf1_project_name', expected '$PROJECT'" >&2
    fi

    # ── Preflight 2: Verify work item type exists in project process ──────────
    local pf2_url="https://dev.azure.com/${ORG}/${PROJECT}/_apis/wit/workitemtypes/${WORK_ITEM_TYPE_ENCODED}?api-version=7.1"
    local pf2_response pf2_code pf2_body

    pf2_response="$(ado_get "$pf2_url")"
    pf2_code="$(echo "$pf2_response" | tail -1)"
    pf2_body="$(echo "$pf2_response" | head -n -1)"

    if is_html_response "$pf2_body"; then
        echo "✗ Preflight FAILED: received HTML from ADO (auth/routing problem)" >&2
        echo "  URL: $pf2_url" >&2
        echo "  Hint: Check AZURE_DEVOPS_PAT and Accept header." >&2
        exit 1
    fi

    if [[ ! "$pf2_code" =~ ^2 ]]; then
        case "$pf2_code" in
            401|403)
                echo "✗ Preflight FAILED: authentication error on type check (HTTP $pf2_code)" >&2
                echo "  Hint: PAT may have lost scope since project check." >&2
                exit 1
                ;;
            404)
                echo "✗ Preflight FAILED: work item type '$WORK_ITEM_TYPE' not found (HTTP $pf2_code)" >&2
                echo "  Hint: This type may not be available in the project's process." >&2
                echo "  Hint: For Agile process, valid types include: User Story, Bug, Task, Epic, Feature." >&2
                exit 1
                ;;
            000)
                echo "✗ Preflight FAILED: request never completed on type check (HTTP $pf2_code)" >&2
                echo "  Request-level failure — got no HTTP response from the server." >&2
                echo "  Hint: Check URL encoding, network connectivity, and curl availability." >&2
                echo "  URL: $pf2_url" >&2
                exit 1
                ;;
            *)
                echo "✗ Preflight FAILED: unexpected response on type check (HTTP $pf2_code)" >&2
                echo "  URL: $pf2_url" >&2
                echo "$pf2_body" >&2
                exit 1
                ;;
        esac
    fi

    echo "✓ Preflight checks passed."
}

# ─── Execute ─────────────────────────────────────────────────────────────────

AUTH_HEADER="$(printf ':%s' "$PAT" | base64 -w 0)"

if [[ "$DRY_RUN" == true ]]; then
    echo "=== DRY RUN ==="
    echo ""
    echo "Preflight 1 (verify org + project + PAT):"
    echo "  curl -s -X GET \"https://dev.azure.com/${ORG}/_apis/projects/${PROJECT}?api-version=7.1\" \\"
    echo "    -H \"Authorization: Basic <base64>\" \\"
    echo "    -H \"Accept: application/json\""
    echo ""
    echo "Preflight 2 (verify work item type):"
    echo "  curl -s -X GET \"https://dev.azure.com/${ORG}/${PROJECT}/_apis/wit/workitemtypes/${WORK_ITEM_TYPE_ENCODED}?api-version=7.1\" \\"
    echo "    -H \"Authorization: Basic <base64>\" \\"
    echo "    -H \"Accept: application/json\""
    echo ""
    echo "Create work item POST:"
    echo "  URL: $API_URL"
    echo "  Body:"
    echo "$BODY" | python3 -m json.tool 2>/dev/null || echo "$BODY"
    echo ""
    echo "  curl -s -X POST \"$API_URL\" \\"
    echo "    -H \"Authorization: Basic <base64>\" \\"
    echo "    -H \"Content-Type: application/json-patch+json\" \\"
    echo "    -H \"Accept: application/json\" \\"
    echo "    -d '$BODY'"
    echo ""
    echo "Run without --dry-run to execute these requests."
    echo "Add --debug to see full request/response detail at runtime."
    exit 0
fi

run_preflights

echo ""
echo "Creating ADO work item..."
echo "  Org:              $ORG"
echo "  Project:          $PROJECT"
echo "  Type:             $WORK_ITEM_TYPE"
echo "  Title:            $TITLE"
echo "  Tags:             $TAGS"
echo "  State:            $STATE"
echo "  Acceptance items: $ACCEPTANCE_COUNT"
echo ""

if [[ "$DEBUG" == true ]]; then
    echo "[DEBUG] POST $API_URL" >&2
    echo "[DEBUG] Headers: Authorization: Basic <redacted>, Content-Type: application/json-patch+json, Accept: application/json" >&2
    echo "[DEBUG] Body:" >&2
    echo "$BODY" >&2
fi

RESPONSE="$(curl -s -w "\n%{http_code}" -X POST "$API_URL" \
    -H "Authorization: Basic ${AUTH_HEADER}" \
    -H "Content-Type: application/json-patch+json" \
    -H "Accept: application/json" \
    -d "$BODY")"

HTTP_CODE="$(echo "$RESPONSE" | tail -1)"
BODY_RESPONSE="$(echo "$RESPONSE" | head -n -1)"

if [[ "$DEBUG" == true ]]; then
    echo "[DEBUG] Response HTTP $HTTP_CODE:" >&2
    echo "$BODY_RESPONSE" >&2
fi

case "$HTTP_CODE" in
    2*)
        # Validate the response is JSON before parsing
        if is_html_response "$BODY_RESPONSE"; then
            echo "✗ Unexpected: ADO returned HTML despite HTTP $HTTP_CODE" >&2
            render_non_json_error "$BODY_RESPONSE" "$HTTP_CODE"
            exit 1
        elif ! is_valid_json "$BODY_RESPONSE"; then
            echo "✗ Unexpected: ADO returned non-JSON response (HTTP $HTTP_CODE)" >&2
            echo "  Response (truncated): $(echo "$BODY_RESPONSE" | head -c 200)" >&2
            exit 1
        fi

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
        ;;
    000)
        echo "✗ Failed to create work item: request never completed (HTTP $HTTP_CODE)" >&2
        echo "  Request-level failure — got no HTTP response from the server." >&2
        echo "  Hint: Check URL, network connectivity, and curl availability." >&2
        echo "  URL: $API_URL" >&2
        exit 1
        ;;
    *)
        echo "✗ Failed to create work item (HTTP $HTTP_CODE)" >&2

        if is_html_response "$BODY_RESPONSE"; then
            render_non_json_error "$BODY_RESPONSE" "$HTTP_CODE"
        else
            # Non-HTML response — try to render JSON or print raw body.
            echo "$BODY_RESPONSE" | python3 -m json.tool 2>/dev/null || echo "$BODY_RESPONSE" >&2
        fi
        exit 1
        ;;
esac
