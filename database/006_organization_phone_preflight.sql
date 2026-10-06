-- Read-only organization phone preflight. Run as the migration identity that can see every organization.
-- No raw phone, owner email, credential or proof is returned. An empty result is required before migration.
-- Keep normalization equivalent to SelfRegistrationValidation.NormalizePhone; no country-code inference.
WITH normalized AS (
    SELECT id, is_active, deleted_at,
           btrim(phone, E' \t\n\r\v\f' || chr(133) || chr(160) || chr(5760) ||
               chr(8192) || chr(8193) || chr(8194) || chr(8195) || chr(8196) || chr(8197) ||
               chr(8198) || chr(8199) || chr(8200) || chr(8201) || chr(8202) ||
               chr(8232) || chr(8233) || chr(8239) || chr(8287) || chr(12288)) AS trimmed,
           regexp_replace(phone, '[^0-9+]', '', 'g') AS canonical_phone
    FROM public.organizations
    WHERE phone IS NOT NULL
), checked AS (
    SELECT *, length(trimmed) BETWEEN 1 AND 50
        AND trimmed !~ '[^0-9+ ()-]'
        AND canonical_phone ~ '^[+]?[0-9]{6,15}$'
        AND (strpos(trimmed, '+') = 0 OR left(trimmed, 1) = '+') AS valid
    FROM normalized
), counted AS (
    SELECT *, count(*) FILTER (WHERE valid) OVER (PARTITION BY canonical_phone) AS same_phone_count
    FROM checked
)
SELECT id AS organization_id, is_active, deleted_at,
       '***' || right(canonical_phone, 3) AS masked_phone,
       CASE WHEN NOT valid THEN 'INVALID_PHONE' ELSE 'DUPLICATE_PHONE' END AS issue,
       same_phone_count
FROM counted
WHERE NOT valid OR same_phone_count > 1
ORDER BY id;
