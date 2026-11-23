-- Weapon cut (#477): weapons.json retires 14 weapons from the loadout. A saved loadout that still names one gets its nearest
-- kept weapon of the same class (assault rifle, SMG, machine gun, shotgun, sniper, pistol). Unlock rows are left alone.

UPDATE loadouts SET primary_weapon = CASE primary_weapon
    WHEN 'g36' THEN 'm4a1'
    WHEN 'ump45' THEN 'p90'
    WHEN 'mp5' THEN 'p90'
    WHEN 'pkm' THEN 'rpk'
    WHEN 'm249' THEN 'rpk'
    WHEN 'm60' THEN 'rpk'
    WHEN 'spas12' THEN 'remington870'
    WHEN 'benelli_m4' THEN 'remington870'
    WHEN 'aa12' THEN 'remington870'
    WHEN 'sniper' THEN 'awm'
    WHEN 'svd' THEN 'awm'
    WHEN 'm82' THEN 'awm'
    ELSE primary_weapon END
WHERE primary_weapon IN ('g36', 'ump45', 'mp5', 'pkm', 'm249', 'm60', 'spas12', 'benelli_m4', 'aa12', 'sniper', 'svd', 'm82');

UPDATE loadouts SET secondary_weapon = CASE secondary_weapon
    WHEN 'pistol' THEN 'deagle'
    WHEN 'glock17' THEN 'deagle'
    ELSE secondary_weapon END
WHERE secondary_weapon IN ('pistol', 'glock17');
