// India reference geography for the tenant-onboarding form: every state / union
// territory with its major cities and district headquarters. Backing data for the
// Country → State → City cascade in NewTenantPanel — NOT an exhaustive gazetteer;
// the form always offers an "Other" free-text escape for towns not listed, so a
// missing entry is an inconvenience, not a blocker. City names are proper nouns
// and intentionally stay in English in both locales (matches address conventions).
//
// Lives in the tenants feature (lazy-loaded with the panel chunk) so the ~9 KB of
// data never lands in the initial bundle.

export interface IndiaState {
  name: string;
  cities: string[];
}

/** The platform currently onboards Indian facilities only (platform.tenants.country
 *  defaults to 'IN'); the country select is single-option until that changes. */
export const COUNTRIES = ['India'] as const;

export const INDIA_STATES: IndiaState[] = [
  {
    name: 'Andaman and Nicobar Islands',
    cities: ['Car Nicobar', 'Mayabunder', 'Port Blair'],
  },
  {
    name: 'Andhra Pradesh',
    cities: [
      'Amaravati', 'Anantapur', 'Bapatla', 'Chittoor', 'Eluru', 'Guntur', 'Kadapa', 'Kakinada',
      'Kurnool', 'Machilipatnam', 'Nandyal', 'Narasaraopet', 'Nellore', 'Ongole', 'Parvathipuram',
      'Rajahmundry', 'Srikakulam', 'Tirupati', 'Vijayawada', 'Visakhapatnam', 'Vizianagaram',
    ],
  },
  {
    name: 'Arunachal Pradesh',
    cities: [
      'Aalo', 'Bomdila', 'Changlang', 'Daporijo', 'Itanagar', 'Khonsa', 'Naharlagun', 'Pasighat',
      'Roing', 'Seppa', 'Tawang', 'Tezu', 'Ziro',
    ],
  },
  {
    name: 'Assam',
    cities: [
      'Barpeta', 'Bongaigaon', 'Dhubri', 'Dibrugarh', 'Diphu', 'Goalpara', 'Golaghat', 'Guwahati',
      'Haflong', 'Hailakandi', 'Hojai', 'Jorhat', 'Karimganj', 'Kokrajhar', 'Mangaldoi', 'Morigaon',
      'Nagaon', 'Nalbari', 'North Lakhimpur', 'Silchar', 'Sivasagar', 'Tezpur', 'Tinsukia',
    ],
  },
  {
    name: 'Bihar',
    cities: [
      'Araria', 'Arrah', 'Arwal', 'Aurangabad', 'Banka', 'Begusarai', 'Bettiah', 'Bhagalpur',
      'Bihar Sharif', 'Buxar', 'Chhapra', 'Darbhanga', 'Dehri', 'Gaya', 'Gopalganj', 'Hajipur',
      'Jamui', 'Jehanabad', 'Katihar', 'Khagaria', 'Kishanganj', 'Lakhisarai', 'Madhepura',
      'Madhubani', 'Motihari', 'Munger', 'Muzaffarpur', 'Nawada', 'Patna', 'Purnia', 'Saharsa',
      'Samastipur', 'Sasaram', 'Sheikhpura', 'Sheohar', 'Sitamarhi', 'Siwan', 'Supaul',
    ],
  },
  {
    name: 'Chandigarh',
    cities: ['Chandigarh'],
  },
  {
    name: 'Chhattisgarh',
    cities: [
      'Ambikapur', 'Baikunthpur', 'Balod', 'Baloda Bazar', 'Balrampur', 'Bemetara', 'Bhilai',
      'Bijapur', 'Bilaspur', 'Dantewada', 'Dhamtari', 'Durg', 'Gariaband', 'Jagdalpur', 'Janjgir',
      'Jashpur', 'Kanker', 'Kawardha', 'Kondagaon', 'Korba', 'Mahasamund', 'Mungeli', 'Narayanpur',
      'Raigarh', 'Raipur', 'Rajnandgaon', 'Sukma', 'Surajpur',
    ],
  },
  {
    name: 'Dadra and Nagar Haveli and Daman and Diu',
    cities: ['Daman', 'Diu', 'Silvassa'],
  },
  {
    name: 'Delhi',
    cities: ['Delhi', 'New Delhi'],
  },
  {
    name: 'Goa',
    cities: ['Bicholim', 'Canacona', 'Curchorem', 'Mapusa', 'Margao', 'Panaji', 'Ponda', 'Vasco da Gama'],
  },
  {
    name: 'Gujarat',
    cities: [
      'Ahmedabad', 'Amreli', 'Anand', 'Bharuch', 'Bhavnagar', 'Bhuj', 'Botad', 'Chhota Udaipur',
      'Dahod', 'Gandhidham', 'Gandhinagar', 'Godhra', 'Himatnagar', 'Jamnagar', 'Junagadh',
      'Lunawada', 'Mehsana', 'Modasa', 'Morbi', 'Nadiad', 'Navsari', 'Palanpur', 'Patan',
      'Porbandar', 'Rajkot', 'Rajpipla', 'Surat', 'Surendranagar', 'Vadodara', 'Valsad', 'Vapi',
      'Veraval', 'Vyara',
    ],
  },
  {
    name: 'Haryana',
    cities: [
      'Ambala', 'Bhiwani', 'Charkhi Dadri', 'Faridabad', 'Fatehabad', 'Gurugram', 'Hisar', 'Jhajjar',
      'Jind', 'Kaithal', 'Karnal', 'Kurukshetra', 'Narnaul', 'Nuh', 'Palwal', 'Panchkula', 'Panipat',
      'Rewari', 'Rohtak', 'Sirsa', 'Sonipat', 'Yamunanagar',
    ],
  },
  {
    name: 'Himachal Pradesh',
    cities: [
      'Baddi', 'Bilaspur', 'Chamba', 'Dharamshala', 'Hamirpur', 'Kangra', 'Keylong', 'Kullu',
      'Mandi', 'Nahan', 'Reckong Peo', 'Shimla', 'Solan', 'Una',
    ],
  },
  {
    name: 'Jammu and Kashmir',
    cities: [
      'Anantnag', 'Bandipora', 'Baramulla', 'Budgam', 'Doda', 'Ganderbal', 'Jammu', 'Kathua',
      'Kishtwar', 'Kulgam', 'Kupwara', 'Poonch', 'Pulwama', 'Rajouri', 'Ramban', 'Reasi', 'Samba',
      'Shopian', 'Sopore', 'Srinagar', 'Udhampur',
    ],
  },
  {
    name: 'Jharkhand',
    cities: [
      'Bokaro Steel City', 'Chaibasa', 'Chatra', 'Daltonganj', 'Deoghar', 'Dhanbad', 'Dumka',
      'Garhwa', 'Giridih', 'Godda', 'Gumla', 'Hazaribagh', 'Jamshedpur', 'Jamtara', 'Khunti',
      'Koderma', 'Latehar', 'Lohardaga', 'Pakur', 'Phusro', 'Ramgarh', 'Ranchi', 'Sahibganj',
      'Seraikela', 'Simdega',
    ],
  },
  {
    name: 'Karnataka',
    cities: [
      'Bagalkot', 'Ballari', 'Belagavi', 'Bengaluru', 'Bidar', 'Chamarajanagar', 'Chikkaballapur',
      'Chikkamagaluru', 'Chitradurga', 'Davanagere', 'Dharwad', 'Gadag', 'Hassan', 'Haveri',
      'Hosapete', 'Hubballi', 'Kalaburagi', 'Karwar', 'Kolar', 'Koppal', 'Madikeri', 'Mandya',
      'Mangaluru', 'Mysuru', 'Raichur', 'Ramanagara', 'Shivamogga', 'Tumakuru', 'Udupi',
      'Vijayapura', 'Yadgir',
    ],
  },
  {
    name: 'Kerala',
    cities: [
      'Alappuzha', 'Kalpetta', 'Kannur', 'Kasaragod', 'Kochi', 'Kollam', 'Kottayam', 'Kozhikode',
      'Malappuram', 'Painavu', 'Palakkad', 'Pathanamthitta', 'Thiruvananthapuram', 'Thrissur',
    ],
  },
  {
    name: 'Ladakh',
    cities: ['Kargil', 'Leh'],
  },
  {
    name: 'Lakshadweep',
    cities: ['Kavaratti'],
  },
  {
    name: 'Madhya Pradesh',
    cities: [
      'Agar Malwa', 'Alirajpur', 'Anuppur', 'Ashoknagar', 'Balaghat', 'Barwani', 'Betul', 'Bhind',
      'Bhopal', 'Burhanpur', 'Chhatarpur', 'Chhindwara', 'Damoh', 'Datia', 'Dewas', 'Dhar',
      'Dindori', 'Guna', 'Gwalior', 'Harda', 'Indore', 'Itarsi', 'Jabalpur', 'Jhabua', 'Katni',
      'Khandwa', 'Khargone', 'Mandla', 'Mandsaur', 'Morena', 'Nagda', 'Narmadapuram', 'Neemuch',
      'Niwari', 'Panna', 'Pithampur', 'Raisen', 'Rajgarh', 'Ratlam', 'Rewa', 'Sagar', 'Satna',
      'Sehore', 'Seoni', 'Shahdol', 'Shajapur', 'Sheopur', 'Shivpuri', 'Sidhi', 'Singrauli',
      'Tikamgarh', 'Ujjain', 'Umaria', 'Vidisha',
    ],
  },
  {
    name: 'Maharashtra',
    cities: [
      'Ahilyanagar', 'Akola', 'Alibag', 'Amravati', 'Beed', 'Bhandara', 'Bhusawal', 'Buldhana',
      'Chandrapur', 'Chhatrapati Sambhajinagar', 'Dharashiv', 'Dhule', 'Gadchiroli', 'Gondia',
      'Hingoli', 'Ichalkaranji', 'Jalgaon', 'Jalna', 'Kalyan-Dombivli', 'Kolhapur', 'Latur',
      'Mumbai', 'Nagpur', 'Nanded', 'Nandurbar', 'Nashik', 'Navi Mumbai', 'Oros', 'Palghar',
      'Panvel', 'Parbhani', 'Pune', 'Ratnagiri', 'Sangli', 'Satara', 'Solapur', 'Thane',
      'Vasai-Virar', 'Wardha', 'Washim', 'Yavatmal',
    ],
  },
  {
    name: 'Manipur',
    cities: [
      'Bishnupur', 'Chandel', 'Churachandpur', 'Imphal', 'Jiribam', 'Kakching', 'Senapati',
      'Tamenglong', 'Thoubal', 'Ukhrul',
    ],
  },
  {
    name: 'Meghalaya',
    cities: [
      'Baghmara', 'Jowai', 'Khliehriat', 'Mawkyrwat', 'Nongpoh', 'Nongstoin', 'Resubelpara',
      'Shillong', 'Tura', 'Williamnagar',
    ],
  },
  {
    name: 'Mizoram',
    cities: [
      'Aizawl', 'Champhai', 'Hnahthial', 'Khawzawl', 'Kolasib', 'Lawngtlai', 'Lunglei', 'Mamit',
      'Saiha', 'Saitual', 'Serchhip',
    ],
  },
  {
    name: 'Nagaland',
    cities: [
      'Chümoukedima', 'Dimapur', 'Kiphire', 'Kohima', 'Longleng', 'Mokokchung', 'Mon', 'Niuland',
      'Noklak', 'Peren', 'Phek', 'Shamator', 'Tseminyu', 'Tuensang', 'Wokha', 'Zunheboto',
    ],
  },
  {
    name: 'Odisha',
    cities: [
      'Angul', 'Balasore', 'Bargarh', 'Baripada', 'Berhampur', 'Bhadrak', 'Bhawanipatna',
      'Bhubaneswar', 'Bolangir', 'Boudh', 'Chhatrapur', 'Cuttack', 'Deogarh', 'Dhenkanal',
      'Jagatsinghpur', 'Jajpur', 'Jeypore', 'Jharsuguda', 'Kendrapara', 'Keonjhar', 'Khordha',
      'Koraput', 'Malkangiri', 'Nabarangpur', 'Nayagarh', 'Nuapada', 'Paralakhemundi', 'Phulbani',
      'Puri', 'Rayagada', 'Rourkela', 'Sambalpur', 'Sonepur', 'Sundargarh',
    ],
  },
  {
    name: 'Puducherry',
    cities: ['Karaikal', 'Mahe', 'Puducherry', 'Yanam'],
  },
  {
    name: 'Punjab',
    cities: [
      'Abohar', 'Amritsar', 'Barnala', 'Bathinda', 'Batala', 'Faridkot', 'Fatehgarh Sahib',
      'Fazilka', 'Firozpur', 'Gurdaspur', 'Hoshiarpur', 'Jalandhar', 'Kapurthala', 'Khanna',
      'Ludhiana', 'Malerkotla', 'Mansa', 'Moga', 'Mohali', 'Muktsar', 'Nawanshahr', 'Pathankot',
      'Patiala', 'Phagwara', 'Rajpura', 'Rupnagar', 'Sangrur', 'Tarn Taran',
    ],
  },
  {
    name: 'Rajasthan',
    cities: [
      'Ajmer', 'Alwar', 'Banswara', 'Baran', 'Barmer', 'Beawar', 'Bharatpur', 'Bhilwara', 'Bikaner',
      'Bundi', 'Chittorgarh', 'Churu', 'Dausa', 'Dhaulpur', 'Dungarpur', 'Gangapur City',
      'Hanumangarh', 'Jaipur', 'Jaisalmer', 'Jalore', 'Jhalawar', 'Jhunjhunu', 'Jodhpur', 'Karauli',
      'Kishangarh', 'Kota', 'Nagaur', 'Pali', 'Pratapgarh', 'Rajsamand', 'Sawai Madhopur', 'Sikar',
      'Sirohi', 'Sri Ganganagar', 'Tonk', 'Udaipur',
    ],
  },
  {
    name: 'Sikkim',
    cities: ['Gangtok', 'Gyalshing', 'Mangan', 'Namchi', 'Pakyong', 'Soreng'],
  },
  {
    name: 'Tamil Nadu',
    cities: [
      'Ariyalur', 'Chengalpattu', 'Chennai', 'Coimbatore', 'Cuddalore', 'Dharmapuri', 'Dindigul',
      'Erode', 'Hosur', 'Kallakurichi', 'Kanchipuram', 'Karur', 'Krishnagiri', 'Kumbakonam',
      'Madurai', 'Mayiladuthurai', 'Nagapattinam', 'Nagercoil', 'Namakkal', 'Neyveli', 'Ooty',
      'Perambalur', 'Pollachi', 'Pudukkottai', 'Rajapalayam', 'Ramanathapuram', 'Ranipet', 'Salem',
      'Sivaganga', 'Sivakasi', 'Tenkasi', 'Thanjavur', 'Theni', 'Thoothukudi', 'Tiruchirappalli',
      'Tirunelveli', 'Tirupathur', 'Tiruppur', 'Tiruvallur', 'Tiruvannamalai', 'Thiruvarur',
      'Vellore', 'Villupuram', 'Virudhunagar',
    ],
  },
  {
    name: 'Telangana',
    cities: [
      'Adilabad', 'Asifabad', 'Bhongir', 'Bhupalpally', 'Bodhan', 'Gadwal', 'Hanamkonda',
      'Hyderabad', 'Jagtial', 'Jangaon', 'Kamareddy', 'Karimnagar', 'Khammam', 'Kothagudem',
      'Mahabubabad', 'Mahbubnagar', 'Mancherial', 'Medak', 'Medchal', 'Miryalaguda', 'Mulugu',
      'Nagarkurnool', 'Nalgonda', 'Narayanpet', 'Nirmal', 'Nizamabad', 'Peddapalli', 'Ramagundam',
      'Sangareddy', 'Siddipet', 'Sircilla', 'Suryapet', 'Vikarabad', 'Wanaparthy', 'Warangal',
    ],
  },
  {
    name: 'Tripura',
    cities: [
      'Agartala', 'Ambassa', 'Belonia', 'Bishalgarh', 'Dharmanagar', 'Kailashahar', 'Khowai',
      'Sabroom', 'Teliamura', 'Udaipur',
    ],
  },
  {
    name: 'Uttar Pradesh',
    cities: [
      'Agra', 'Aligarh', 'Akbarpur', 'Amethi', 'Amroha', 'Auraiya', 'Ayodhya', 'Azamgarh',
      'Baghpat', 'Bahraich', 'Ballia', 'Balrampur', 'Banda', 'Barabanki', 'Bareilly', 'Basti',
      'Bhinga', 'Bijnor', 'Budaun', 'Bulandshahr', 'Chandauli', 'Chitrakoot', 'Deoria', 'Etah',
      'Etawah', 'Farrukhabad', 'Fatehpur', 'Firozabad', 'Ghaziabad', 'Ghazipur', 'Gonda',
      'Gorakhpur', 'Gyanpur', 'Hamirpur', 'Hapur', 'Hardoi', 'Hathras', 'Jaunpur', 'Jhansi',
      'Kannauj', 'Kasganj', 'Khalilabad', 'Lakhimpur Kheri', 'Lalitpur', 'Lucknow', 'Maharajganj',
      'Mahoba', 'Mainpuri', 'Manjhanpur', 'Mathura', 'Mau', 'Meerut', 'Mirzapur', 'Moradabad',
      'Muzaffarnagar', 'Noida', 'Orai', 'Padrauna', 'Pilibhit', 'Pratapgarh', 'Prayagraj',
      'Raebareli', 'Rampur', 'Robertsganj', 'Saharanpur', 'Sambhal', 'Shahjahanpur', 'Shamli',
      'Siddharthnagar', 'Sitapur', 'Sultanpur', 'Unnao', 'Varanasi',
    ],
  },
  {
    name: 'Uttarakhand',
    cities: [
      'Almora', 'Bageshwar', 'Champawat', 'Dehradun', 'Gopeshwar', 'Haldwani', 'Haridwar',
      'Kashipur', 'Khatima', 'Kotdwar', 'Nainital', 'New Tehri', 'Pauri', 'Pithoragarh',
      'Rishikesh', 'Roorkee', 'Rudraprayag', 'Rudrapur', 'Srinagar (Garhwal)', 'Uttarkashi',
      'Vikasnagar',
    ],
  },
  {
    name: 'West Bengal',
    cities: [
      'Alipurduar', 'Asansol', 'Baharampur', 'Balurghat', 'Bangaon', 'Bankura', 'Barasat',
      'Bardhaman', 'Barrackpore', 'Basirhat', 'Chinsurah', 'Contai', 'Cooch Behar', 'Darjeeling',
      'Diamond Harbour', 'Durgapur', 'English Bazar', 'Habra', 'Haldia', 'Howrah', 'Jalpaiguri',
      'Jhargram', 'Kalimpong', 'Kharagpur', 'Kolkata', 'Krishnanagar', 'Medinipur', 'Purulia',
      'Raiganj', 'Ranaghat', 'Siliguri', 'Suri', 'Tamluk',
    ],
  },
];
